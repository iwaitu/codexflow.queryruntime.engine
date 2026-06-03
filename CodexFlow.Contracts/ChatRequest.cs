using System;
using System.Collections.Generic;
using System.Text;

namespace CodexFlow.Contracts
{
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string GitRepoUrl { get; set; } = string.Empty;
        public string Branch { get; set; } = "master";
        public bool ApproveGitPush { get; set; }
        public string? ConversationId { get; set; }
        public string? SessionId { get; set; } = string.Empty;
        public string? ReplyId { get; set; }
        public string SystemPrompt { get; set; } = string.Empty;
        public string? TaskId { get; set; }
    }
}
