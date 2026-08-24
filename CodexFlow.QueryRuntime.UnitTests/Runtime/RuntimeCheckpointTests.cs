using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Runtime;

public sealed class RuntimeCheckpointTests
{
    [Fact]
    public async Task RunAsync_WritesOrderedStableCheckpointsAndTerminalLineage()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var model = new RecordingModelClient(_ => Text("complete"));
        var result = await new RuntimeAgentLoop(model).RunAsync(
            CreateRequest(checkpoints),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal(
            [
                RuntimeCheckpointKind.TurnStarted,
                RuntimeCheckpointKind.StepPrepared,
                RuntimeCheckpointKind.ModelCommitted,
                RuntimeCheckpointKind.StepCommitted,
                RuntimeCheckpointKind.Terminal
            ],
            checkpoints.Checkpoints.Select(static checkpoint => checkpoint.Kind));
        Assert.Equal(
            Enumerable.Range(1, checkpoints.Checkpoints.Count).Select(static value => (long)value),
            checkpoints.Checkpoints.Select(static checkpoint => checkpoint.Sequence));
        Assert.All(checkpoints.Checkpoints, checkpoint =>
        {
            Assert.Equal(result.Attempt, checkpoint.Attempt);
            Assert.Equal("checkpoint-session", checkpoint.Request.SessionId.Value);
            Assert.Equal(RuntimeCheckpointSchema.RuntimeContractVersion, checkpoint.RuntimeContractVersion);
        });
        Assert.Equal(RuntimeCheckpointDisposition.Terminal, checkpoints.Latest!.Disposition);
    }

    [Fact]
    public async Task ResumeAsync_FromStepPreparedCreatesChildAttemptAndSamplesOnce()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("source")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepPrepared);
        var resumedCheckpoints = new InMemoryRuntimeCheckpointSink();
        var resumedModel = new RecordingModelClient(_ => Text("resumed"));
        var attempt = RuntimeRunAttempt.Resume(checkpoint, "attempt-resumed");
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = attempt,
            CheckpointSink = resumedCheckpoints
        };

        var result = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request,
            checkpoint,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("resumed", result.FinalText);
        Assert.Single(resumedModel.Requests);
        Assert.Equal(checkpoint.Request.TurnId, result.Turn.Context.TurnId);
        Assert.Equal(checkpoint.Attempt.AttemptId, result.Attempt!.ParentAttemptId);
        Assert.Equal(checkpoint.Attempt.RootAttemptId, result.Attempt.RootAttemptId);
        Assert.Equal(checkpoint.Attempt.Ordinal + 1, result.Attempt.Ordinal);
    }

    [Fact]
    public async Task ResumeAsync_FromTextModelCommitDoesNotCallProviderAgain()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("durable text")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ModelCommitted);
        var resumedModel = new RecordingModelClient(_ => throw new InvalidOperationException("provider must not run"));
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };

        var result = await new RuntimeAgentLoop(resumedModel).ResumeAsync(
            request,
            checkpoint,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("durable text", result.FinalText);
        Assert.Empty(resumedModel.Requests);
        Assert.Contains(result.History, static message =>
            message.Role == RuntimeMessageRole.Assistant &&
            message.Items.OfType<RuntimeTextItem>().Any(item => item.Text == "durable text"));
    }

    [Fact]
    public async Task ResumeAsync_FromUnresolvedToolBoundaryRequiresReconciliationBeforeExecution()
    {
        var call = ToolCall("call-1", "read_state");
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var executor = new RecordingExecutor();
        var sourceModel = new RecordingModelClient(
            _ => Tool(call),
            _ => Text("done"));
        await new RuntimeAgentLoop(sourceModel).RunAsync(
            CreateRequest(checkpoints, [ToolDescriptor("read_state")], executor),
            ct: TestContext.Current.CancellationToken);
        var checkpoint = Assert.Single(checkpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ModelCommitted &&
            value.Disposition == RuntimeCheckpointDisposition.NeedsReconciliation);
        var resumeModel = new RecordingModelClient(_ => Text("must not execute"));
        var resumeExecutor = new RecordingExecutor();
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink(),
            ToolExecutor = resumeExecutor
        };

        var error = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(resumeModel).ResumeAsync(
                request,
                checkpoint,
                ct: TestContext.Current.CancellationToken));

        Assert.Equal(RuntimeErrorCategory.UncertainSideEffect, error.Error.Category);
        Assert.Equal("checkpoint_needs_reconciliation", error.Error.Code);
        Assert.Empty(resumeModel.Requests);
        Assert.Empty(resumeExecutor.Calls);
    }

    [Fact]
    public async Task ResumeAsync_FromCommittedToolBatchDoesNotRepeatToolExecution()
    {
        var call = ToolCall("call-committed", "read_state");
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var sourceExecutor = new RecordingExecutor(new string('x', 9_000));
        await new RuntimeAgentLoop(new RecordingModelClient(
            _ => Tool(call),
            _ => Text("source final"))).RunAsync(
            CreateRequest(checkpoints, [ToolDescriptor("read_state")], sourceExecutor),
            ct: TestContext.Current.CancellationToken);
        Assert.Single(sourceExecutor.Calls);
        var checkpoint = Assert.Single(checkpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        var checkpointBlob = Assert.Single(checkpoint.HistoryBlobs);
        var resumeExecutor = new RecordingExecutor();
        var resumeModel = new RecordingModelClient(_ => Text("resumed final"));
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink(),
            ToolExecutor = resumeExecutor
        };

        var result = await new RuntimeAgentLoop(resumeModel).ResumeAsync(
            request,
            checkpoint,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        Assert.Equal("resumed final", result.FinalText);
        Assert.Single(resumeModel.Requests);
        Assert.Empty(resumeExecutor.Calls);
        Assert.Equal(1, result.Turn.Progress.ToolCallCount);
        Assert.Equal(2, result.Turn.Steps.Count);
        var resumedBlob = Assert.Single(result.HistoryBlobs.Values);
        Assert.Equal(checkpointBlob.Digest, resumedBlob.Digest);
        Assert.Equal(checkpointBlob.Data.ToArray(), resumedBlob.Data.ToArray());
    }

    [Fact]
    public async Task ResumeAsync_PreservesCanonicalHistoryIdsAcrossCommittedVersions()
    {
        var call = ToolCall("call-stable-history", "read_state");
        var sourceCheckpoints = new InMemoryRuntimeCheckpointSink();
        var sourceRequest = CreateRequest(
            sourceCheckpoints,
            [ToolDescriptor("read_state")],
            new RecordingExecutor("stable tool result")) with
        {
            InitialMessages =
            [
                new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("stable system")]),
                new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("stable system")]),
                new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])
            ]
        };
        await new RuntimeAgentLoop(new RecordingModelClient(
            _ => Tool(call),
            _ => Text("source final"))).RunAsync(
            sourceRequest,
            ct: TestContext.Current.CancellationToken);
        var source = Assert.Single(sourceCheckpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        Assert.True(source.CanonicalHistory.Select(static entry => entry.CommittedVersion).Distinct().Count() >= 2);
        Assert.Contains(source.CanonicalHistory, static entry => entry.Id.Value == "h0:m0");
        Assert.Contains(source.CanonicalHistory, static entry => entry.Id.Value == "h0:m2");
        Assert.DoesNotContain(source.CanonicalHistory, static entry => entry.Id.Value == "h0:m1");

        var resumedCheckpoints = new InMemoryRuntimeCheckpointSink();
        var request = source.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(source),
            CheckpointSink = resumedCheckpoints,
            ToolExecutor = new RecordingExecutor()
        };
        var result = await new RuntimeAgentLoop(new RecordingModelClient(_ => Text("resumed final"))).ResumeAsync(
            request,
            source,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        var resumed = resumedCheckpoints.Latest!;
        Assert.True(resumed.CanonicalHistory.Count > source.CanonicalHistory.Count);
        for (var index = 0; index < source.CanonicalHistory.Count; index++)
        {
            Assert.Equal(source.CanonicalHistory[index].Id, resumed.CanonicalHistory[index].Id);
            Assert.Equal(
                source.CanonicalHistory[index].CommittedVersion,
                resumed.CanonicalHistory[index].CommittedVersion);
            Assert.Equal(
                source.CanonicalHistory[index].ItemIds,
                resumed.CanonicalHistory[index].ItemIds);
        }
    }

    [Fact]
    public async Task ResumeAsync_PreservesTrailingAndAllOmittedHistorySequenceGaps()
    {
        var scenarios = new[]
        {
            new
            {
                Initial = (IReadOnlyList<RuntimeMessage>)
                [
                    new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("same")]),
                    new RuntimeMessage(RuntimeMessageRole.System, [new RuntimeTextItem("same")])
                ],
                Next = 2L,
                ExpectedCanonicalIds = new[] { "h0:m0" }
            },
            new
            {
                Initial = (IReadOnlyList<RuntimeMessage>)
                [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem(string.Empty)])],
                Next = 1L,
                ExpectedCanonicalIds = Array.Empty<string>()
            }
        };

        foreach (var scenario in scenarios)
        {
            var sourceCheckpoints = new InMemoryRuntimeCheckpointSink();
            var sourceRequest = CreateRequest(sourceCheckpoints) with
            {
                InitialMessages = scenario.Initial
            };
            await new RuntimeAgentLoop(new RecordingModelClient(_ => Text("source"))).RunAsync(
                sourceRequest,
                ct: TestContext.Current.CancellationToken);
            var prepared = Assert.Single(sourceCheckpoints.Checkpoints, static value =>
                value.Kind == RuntimeCheckpointKind.StepPrepared);
            Assert.Equal(scenario.Next, prepared.NextHistoryMessageSequence);
            Assert.Equal(
                scenario.ExpectedCanonicalIds,
                prepared.CanonicalHistory.Select(static entry => entry.Id.Value));

            var resumedCheckpoints = new InMemoryRuntimeCheckpointSink();
            var request = prepared.Request.ToLoopRequest() with
            {
                Attempt = RuntimeRunAttempt.Resume(prepared),
                CheckpointSink = resumedCheckpoints
            };
            var result = await new RuntimeAgentLoop(new RecordingModelClient(_ => Text("resumed"))).ResumeAsync(
                request,
                prepared,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
            var assistant = Assert.Single(
                resumedCheckpoints.Latest!.CanonicalHistory,
                static entry => entry.Message.Role == RuntimeMessageRole.Assistant);
            Assert.Equal($"h1:m{scenario.Next}", assistant.Id.Value);
            Assert.Equal(scenario.Next + 1, resumedCheckpoints.Latest.NextHistoryMessageSequence);
        }
    }

    [Fact]
    public async Task ResumeAsync_TerminalCheckpointAndBrokenLineageFailBeforeProviderCall()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("terminal")));
        var terminal = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.Terminal);
        var model = new RecordingModelClient(_ => Text("must not run"));
        var terminalRequest = terminal.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(terminal),
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };

        var terminalError = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(model).ResumeAsync(
                terminalRequest,
                terminal,
                ct: TestContext.Current.CancellationToken));
        Assert.Equal("checkpoint_already_terminal", terminalError.Error.Code);

        var prepared = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepPrepared);
        var brokenAttempt = RuntimeRunAttempt.Create("unrelated-attempt");
        var brokenRequest = prepared.Request.ToLoopRequest() with
        {
            Attempt = brokenAttempt,
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };
        var lineageError = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(model).ResumeAsync(
                brokenRequest,
                prepared,
                ct: TestContext.Current.CancellationToken));
        Assert.Equal("checkpoint_attempt_lineage_invalid", lineageError.Error.Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task ResumeAsync_RequestMismatchFailsBeforeProviderCall()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("source")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepPrepared);
        var model = new RecordingModelClient(_ => Text("must not run"));
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Policy = new RuntimePolicySnapshot("changed", "repair"),
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink()
        };

        var error = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(model).ResumeAsync(
                request,
                checkpoint,
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("checkpoint_request_mismatch", error.Error.Code);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task JsonStore_RoundTripsAndRejectsTampering()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("persist me")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepCommitted);
        var root = Directory.CreateTempSubdirectory("qre-h1-checkpoint-");
        try
        {
            var store = new RuntimeJsonCheckpointStore(root.FullName);
            await store.SaveAsync(checkpoint, TestContext.Current.CancellationToken);
            var roundTrip = RuntimeJsonCheckpointStore.Read(store.CheckpointPath);

            Assert.Equal(checkpoint.Kind, roundTrip.Kind);
            Assert.Equal(checkpoint.RequestFingerprint, roundTrip.RequestFingerprint);
            Assert.Equal(checkpoint.CanonicalHistory.Count, roundTrip.CanonicalHistory.Count);
            Assert.Equal(checkpoint.NextHistoryMessageSequence, roundTrip.NextHistoryMessageSequence);
            for (var index = 0; index < checkpoint.CanonicalHistory.Count; index++)
            {
                Assert.Equal(checkpoint.CanonicalHistory[index].Id, roundTrip.CanonicalHistory[index].Id);
                Assert.Equal(
                    checkpoint.CanonicalHistory[index].CommittedVersion,
                    roundTrip.CanonicalHistory[index].CommittedVersion);
                Assert.Equal(
                    checkpoint.CanonicalHistory[index].ItemIds,
                    roundTrip.CanonicalHistory[index].ItemIds);
            }

            var bytes = await File.ReadAllBytesAsync(store.CheckpointPath, TestContext.Current.CancellationToken);
            var marker = System.Text.Encoding.UTF8.GetBytes("persist me");
            var offset = bytes.AsSpan().IndexOf(marker);
            Assert.True(offset >= 0);
            bytes[offset] ^= 1;
            await File.WriteAllBytesAsync(store.CheckpointPath, bytes, TestContext.Current.CancellationToken);

            var error = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath));
            Assert.Equal("checkpoint_integrity_invalid", error.Error.Code);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_RejectsTruncatedAndOversizedFilesBeforeRecovery()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("bounded")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepCommitted);
        var root = Directory.CreateTempSubdirectory("qre-h1-bounds-");
        try
        {
            var store = new RuntimeJsonCheckpointStore(root.FullName);
            await File.WriteAllTextAsync(
                store.CheckpointPath,
                "{\"schemaVersion\":1",
                TestContext.Current.CancellationToken);

            var truncated = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath));
            Assert.Equal("checkpoint_json_invalid", truncated.Error.Code);

            await File.WriteAllTextAsync(
                store.CheckpointPath,
                "{} trailing-data",
                TestContext.Current.CancellationToken);
            var trailing = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath));
            Assert.Equal("checkpoint_json_invalid", trailing.Error.Code);

            await File.WriteAllTextAsync(
                store.CheckpointPath,
                "{\"a\":{\"b\":{\"c\":{\"d\":{\"e\":1}}}}}",
                TestContext.Current.CancellationToken);
            var depth = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(
                    store.CheckpointPath,
                    new RuntimeJsonCheckpointStoreOptions { MaxJsonDepth = 4 }));
            Assert.Equal("checkpoint_json_invalid", depth.Error.Code);

            var restrictive = new RuntimeJsonCheckpointStoreOptions { MaxFileBytes = 512 };
            await File.WriteAllBytesAsync(
                store.CheckpointPath,
                new byte[restrictive.MaxFileBytes + 1],
                TestContext.Current.CancellationToken);
            Assert.Throws<InvalidDataException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath, restrictive));

            var boundedStore = new RuntimeJsonCheckpointStore(
                Path.Combine(root.FullName, "save"),
                restrictive);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await boundedStore.SaveAsync(checkpoint, TestContext.Current.CancellationToken));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_RejectsIncompatibleRuntimeAndMalformedDigest()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("compatibility")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepCommitted);
        var root = Directory.CreateTempSubdirectory("qre-h1-compat-");
        try
        {
            var store = new RuntimeJsonCheckpointStore(root.FullName);
            await WriteEnvelopeAsync(
                store.CheckpointPath,
                checkpoint with { RuntimeContractVersion = "qre-v2-h1-future" });

            var incompatible = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath));
            Assert.Equal("checkpoint_runtime_incompatible", incompatible.Error.Code);

            await WriteEnvelopeAsync(store.CheckpointPath, checkpoint, "not-hex");
            var malformed = Assert.Throws<RuntimeResumeException>(() =>
                RuntimeJsonCheckpointStore.Read(store.CheckpointPath));
            Assert.Equal("checkpoint_integrity_invalid", malformed.Error.Code);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_RejectsNonCanonicalCheckpointFilename()
    {
        var root = Directory.CreateTempSubdirectory("qre-h1-path-");
        try
        {
            var path = Path.Combine(root.FullName, "renamed-checkpoint.json");
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken);

            Assert.Throws<InvalidDataException>(() => RuntimeJsonCheckpointStore.Read(path));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task JsonStore_PrivateCheckpointUsesOwnerOnlyUnixPermissions()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("private")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepCommitted);
        var root = Directory.CreateTempSubdirectory("qre-h1-private-");
        var runDirectory = Path.Combine(root.FullName, "private-run");
        try
        {
            var store = new RuntimeJsonCheckpointStore(
                runDirectory,
                new RuntimeJsonCheckpointStoreOptions { Private = true });
            await store.SaveAsync(checkpoint, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(store.CheckpointPath));
            if (!OperatingSystem.IsWindows())
            {
                var forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                    UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(runDirectory) & forbidden);
                Assert.Equal((UnixFileMode)0, File.GetUnixFileMode(store.CheckpointPath) & forbidden);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_RejectsNonNormalizedRecoveryCompatibilityId()
    {
        var request = CreateRequest(new InMemoryRuntimeCheckpointSink()) with
        {
            RecoveryCompatibilityId = " unit-test-h1:v1 "
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new RuntimeAgentLoop(new RecordingModelClient(_ => Text(
                "must not run",
                TestContext.Current.CancellationToken))).RunAsync(
                request,
                ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckpointWriteFailure_FailsClosedAndDurableResumeRejectsBestEffort()
    {
        var failClosedModel = new RecordingModelClient(_ => Text("must not sample"));
        var failClosedRequest = CreateRequest(new ThrowingCheckpointSink());

        var failed = await new RuntimeAgentLoop(failClosedModel).RunAsync(
            failClosedRequest,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Failed, failed.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, failed.TerminationReason);
        Assert.Equal("checkpoint_write_failed", failed.Error?.Code);
        Assert.Empty(failClosedModel.Requests);

        var invalidModeModel = new RecordingModelClient(_ => Text("must not sample"));
        var invalidModeRequest = CreateRequest(new ThrowingCheckpointSink()) with
        {
            CheckpointFailureMode = (RuntimeCheckpointFailureMode)1
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new RuntimeAgentLoop(invalidModeModel).RunAsync(
                invalidModeRequest,
                ct: TestContext.Current.CancellationToken));
        Assert.Empty(invalidModeModel.Requests);
    }

    [Fact]
    public async Task ResumeAsync_RejectsInvalidCommittedModelOutputBeforeProviderCall()
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var sourceRequest = CreateRequest(checkpoints) with
        {
            Budget = new RuntimeBudgetSnapshot(3, 4, maxOutputTokens: 5, maxContinuations: 1)
        };
        var sourceResult = await new RuntimeAgentLoop(new RecordingModelClient(_ => Text("valid"))).RunAsync(
            sourceRequest,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeTurnStatus.Completed, sourceResult.Status);
        var checkpoint = Assert.Single(checkpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ModelCommitted);

        var overBudget = MutateCommittedModelOutput(
            checkpoint,
            output => output with
            {
                Usage = output.Usage with { OutputTokens = 6, TotalTokens = 10 }
            });
        var cancelled = MutateCommittedModelOutput(
            checkpoint,
            output => output with { StopReason = RuntimeModelStopReason.Cancelled });
        var empty = MutateCommittedModelOutput(
            checkpoint,
            output => output with { Items = Array.Empty<RuntimeItem>() });

        foreach (var invalid in new[] { overBudget, cancelled, empty })
        {
            var model = new RecordingModelClient(_ => Text("must not run"));
            var request = invalid.Request.ToLoopRequest() with
            {
                Attempt = RuntimeRunAttempt.Resume(invalid),
                CheckpointSink = new InMemoryRuntimeCheckpointSink()
            };
            var error = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
                new RuntimeAgentLoop(model).ResumeAsync(
                    request,
                    invalid,
                    ct: TestContext.Current.CancellationToken));
            Assert.Equal("checkpoint_model_output_invalid", error.Error.Code);
            Assert.Empty(model.Requests);
        }
    }

    [Fact]
    public async Task RunAsync_FatalToolBatchNeverBecomesResumableCheckpoint()
    {
        var call = ToolCall("approval-call", "write_state");
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var request = CreateRequest(
            checkpoints,
            [ToolDescriptor("write_state")],
            new RecordingExecutor()) with
        {
            ToolAuthorization = new RequireApprovalAuthorization()
        };

        var result = await new RuntimeAgentLoop(new RecordingModelClient(_ => Tool(call))).RunAsync(
            request,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeTurnStatus.Failed, result.Status);
        Assert.Equal(RuntimeTerminationReason.FailClosed, result.TerminationReason);
        Assert.DoesNotContain(checkpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ToolBatchCommitted);
        Assert.Contains(checkpoints.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.ModelCommitted &&
            value.Disposition == RuntimeCheckpointDisposition.NeedsReconciliation);
    }

    [Fact]
    public async Task CheckpointFailureWindows_StopBeforeToolOrLeaveReconciliationBoundary()
    {
        var call = ToolCall("failure-window-call", "read_state");

        var beforeTool = new FailFromCheckpointKindSink(RuntimeCheckpointKind.ModelCommitted);
        var beforeToolExecutor = new RecordingExecutor();
        var beforeToolResult = await new RuntimeAgentLoop(
            new RecordingModelClient(_ => Tool(call))).RunAsync(
            CreateRequest(beforeTool, [ToolDescriptor("read_state")], beforeToolExecutor),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeTurnStatus.Failed, beforeToolResult.Status);
        Assert.Equal("checkpoint_write_failed", beforeToolResult.Error?.Code);
        Assert.Empty(beforeToolExecutor.Calls);
        Assert.Equal(RuntimeCheckpointKind.StepPrepared, beforeTool.Saved[^1].Kind);

        var afterTool = new FailFromCheckpointKindSink(RuntimeCheckpointKind.ToolBatchCommitted);
        var afterToolExecutor = new RecordingExecutor();
        var afterToolResult = await new RuntimeAgentLoop(
            new RecordingModelClient(_ => Tool(call))).RunAsync(
            CreateRequest(afterTool, [ToolDescriptor("read_state")], afterToolExecutor),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeTurnStatus.Failed, afterToolResult.Status);
        Assert.Single(afterToolExecutor.Calls);
        Assert.Equal(RuntimeCheckpointKind.ModelCommitted, afterTool.Saved[^1].Kind);
        Assert.Equal(RuntimeCheckpointDisposition.NeedsReconciliation, afterTool.Saved[^1].Disposition);
    }

    [Fact]
    public async Task FindLatestCheckpoint_FollowsLeafAttemptAndNeverReopensClaimedParent()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("lineage")));
        var parent = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepPrepared);
        var terminal = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.Terminal);
        var root = Directory.CreateTempSubdirectory("qre-h1-lineage-");
        try
        {
            var parentStore = new RuntimeJsonCheckpointStore(Path.Combine(
                root.FullName, ".qre", "v2", "runs", "parent"));
            await parentStore.SaveAsync(parent, TestContext.Current.CancellationToken);
            var childAttempt = RuntimeRunAttempt.Resume(parent, "attempt-child");
            var childPrepared = parent with
            {
                Sequence = 1,
                Attempt = childAttempt,
                CreatedAt = parent.CreatedAt.AddSeconds(1)
            };
            var childStore = new RuntimeJsonCheckpointStore(Path.Combine(
                root.FullName, ".qre", "v2", "runs", "child"));
            await childStore.SaveAsync(childPrepared, TestContext.Current.CancellationToken);

            Assert.Equal(
                childStore.CheckpointPath,
                RuntimeJsonCheckpointStore.FindLatestCheckpoint(root.FullName));

            var childTerminal = terminal with
            {
                Sequence = 2,
                Attempt = childAttempt,
                CreatedAt = terminal.CreatedAt.AddSeconds(2)
            };
            await childStore.SaveAsync(childTerminal, TestContext.Current.CancellationToken);

            Assert.Throws<FileNotFoundException>(() =>
                RuntimeJsonCheckpointStore.FindLatestCheckpoint(root.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResumeAsync_DynamicToolCatalogFailsBeforeProviderOrToolExecution()
    {
        var source = await CaptureAsync(new RecordingModelClient(_ => Text("source")));
        var checkpoint = Assert.Single(source.Checkpoints, static value =>
            value.Kind == RuntimeCheckpointKind.StepPrepared);
        var model = new RecordingModelClient(_ => Text("must not run"));
        var request = checkpoint.Request.ToLoopRequest() with
        {
            Attempt = RuntimeRunAttempt.Resume(checkpoint),
            CheckpointSink = new InMemoryRuntimeCheckpointSink(),
            ToolCatalogSelector = new PassThroughSelector()
        };

        var error = await Assert.ThrowsAsync<RuntimeResumeException>(() =>
            new RuntimeAgentLoop(model).ResumeAsync(
                request,
                checkpoint,
                ct: TestContext.Current.CancellationToken));

        Assert.Equal("resume_dynamic_tool_catalog_unsupported", error.Error.Code);
        Assert.Empty(model.Requests);
    }

    private static async Task<InMemoryRuntimeCheckpointSink> CaptureAsync(
        RecordingModelClient model)
    {
        var checkpoints = new InMemoryRuntimeCheckpointSink();
        var result = await new RuntimeAgentLoop(model).RunAsync(
            CreateRequest(checkpoints),
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeTurnStatus.Completed, result.Status);
        return checkpoints;
    }

    private static RuntimeCheckpointDocument MutateCommittedModelOutput(
        RuntimeCheckpointDocument checkpoint,
        Func<RuntimeModelOutput, RuntimeModelOutput> mutate)
    {
        var turn = checkpoint.Session.ActiveTurn!;
        var steps = turn.Steps.ToArray();
        var output = mutate(steps[^1].Output!);
        steps[^1] = steps[^1] with { Output = output };
        var updatedTurn = turn with
        {
            Steps = Array.AsReadOnly(steps),
            Progress = turn.Progress with
            {
                Usage = output.Usage,
                LastModelStopReason = output.StopReason
            }
        };
        return checkpoint with
        {
            Session = checkpoint.Session with { ActiveTurn = updatedTurn }
        };
    }

    private static async Task WriteEnvelopeAsync(
        string path,
        RuntimeCheckpointDocument checkpoint,
        string? digest = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            checkpoint,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointDocument);
        var envelope = new RuntimeCheckpointFileEnvelope(
            RuntimeCheckpointSchema.CurrentVersion,
            "qre.v2.checkpoint",
            payload.LongLength,
            digest ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            checkpoint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            RuntimeCheckpointJsonContext.Default.RuntimeCheckpointFileEnvelope);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
    }

    private static RuntimeAgentLoopRequest CreateRequest(
        IRuntimeCheckpointSink checkpoints,
        IReadOnlyList<RuntimeToolDescriptor>? tools = null,
        IRuntimeToolExecutor? executor = null)
        => new(
            new RuntimeSessionId("checkpoint-session"),
            new RuntimeTurnId("checkpoint-turn"),
            "checkpoint objective",
            [new RuntimeMessage(RuntimeMessageRole.User, [new RuntimeTextItem("start")])],
            tools ?? [],
            new RuntimeModelParameters(Model: "test-model"),
            new RuntimePolicySnapshot("policy-v1", "readonly"),
            new RuntimeEnvironmentSnapshot("local", "workspace", "sha256:test"),
            new RuntimeBudgetSnapshot(3, 4, maxContinuations: 1),
            CreatedAt: DateTimeOffset.UnixEpoch)
        {
            Attempt = RuntimeRunAttempt.Create("attempt-source"),
            CheckpointSink = checkpoints,
            RecoveryCompatibilityId = "unit-test-h1:v1",
            ToolExecutor = executor
        };

    private static RuntimeToolDescriptor ToolDescriptor(string name)
    {
        using var schema = JsonDocument.Parse("{\"type\":\"object\"}");
        return new RuntimeToolDescriptor(
            name,
            "1",
            "test tool",
            schema.RootElement.Clone(),
            RuntimeToolSideEffect.ReadOnly,
            RuntimeToolIdempotency.Idempotent);
    }

    private static RuntimeToolCall ToolCall(string invocationId, string name)
    {
        using var arguments = JsonDocument.Parse("{}");
        return new RuntimeToolCall(
            new RuntimeInvocationId(invocationId),
            name,
            arguments.RootElement.Clone());
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Text(
        string text,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new RuntimeTextDeltaEvent(text);
        yield return new RuntimeUsageEvent(new RuntimeUsage(4, 2, 6));
        yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn);
    }

    private static async IAsyncEnumerable<RuntimeModelStreamEvent> Tool(
        RuntimeToolCall call,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await Task.Yield();
        yield return new RuntimeToolCallEvent(call);
        yield return new RuntimeModelCompletedEvent(RuntimeModelStopReason.ToolCall);
    }

    private sealed class RecordingModelClient(
        params Func<RuntimeModelRequest, IAsyncEnumerable<RuntimeModelStreamEvent>>[] scripts)
        : IRuntimeModelClient
    {
        private int _index;

        public List<RuntimeModelRequest> Requests { get; } = [];

        public IAsyncEnumerable<RuntimeModelStreamEvent> StreamAsync(
            RuntimeModelRequest request,
            CancellationToken ct = default)
        {
            Requests.Add(request);
            var index = Interlocked.Increment(ref _index) - 1;
            return WithCancellation(scripts[index](request), ct);
        }

        private static async IAsyncEnumerable<RuntimeModelStreamEvent> WithCancellation(
            IAsyncEnumerable<RuntimeModelStreamEvent> source,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var runtimeEvent in source.WithCancellation(ct))
            {
                yield return runtimeEvent;
            }
        }
    }

    private sealed class RecordingExecutor(string resultText = "ok") : IRuntimeToolExecutor
    {
        public List<RuntimeToolCall> Calls { get; } = [];

        public ValueTask<RuntimeToolResult> ExecuteAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
        {
            Calls.Add(call);
            return ValueTask.FromResult(new RuntimeToolResult(call.InvocationId, resultText, true));
        }
    }

    private sealed class ThrowingCheckpointSink : IRuntimeCheckpointSink
    {
        public ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
            => ValueTask.FromException(new IOException("simulated checkpoint failure"));
    }

    private sealed class FailFromCheckpointKindSink(RuntimeCheckpointKind firstFailure)
        : IRuntimeCheckpointSink
    {
        private bool _failed;

        public List<RuntimeCheckpointDocument> Saved { get; } = [];

        public ValueTask SaveAsync(RuntimeCheckpointDocument checkpoint, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_failed || checkpoint.Kind == firstFailure)
            {
                _failed = true;
                return ValueTask.FromException(new IOException("simulated crash-window write failure"));
            }
            Saved.Add(checkpoint);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PassThroughSelector : IRuntimeToolCatalogSelector
    {
        public IReadOnlyList<RuntimeToolDescriptor> SelectTools(
            PreparedRuntimeContext context,
            IReadOnlyList<RuntimeToolDescriptor> frozenCatalog,
            int stepIndex)
            => frozenCatalog;

        public void Observe(RuntimeToolCall call, RuntimeToolResult result)
        {
        }
    }

    private sealed class RequireApprovalAuthorization : IRuntimeToolAuthorization
    {
        public ValueTask<RuntimeToolAuthorizationDecision> AuthorizeAsync(
            RuntimeToolDescriptor descriptor,
            RuntimeToolCall call,
            RuntimeToolExecutionContext context,
            CancellationToken ct)
            => ValueTask.FromResult(RuntimeToolAuthorizationDecision.RequireApproval());
    }
}
