#!/usr/bin/env node
// Small helper for exposing Node.js functions as QRE stdio tools.

import { mkdir, writeFile, realpath } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const tools = new Map();

export function qreTool(definition, handler) {
  const name = definition.name;
  if (!name || typeof name !== "string") {
    throw new Error("QRE tool definition requires a string name.");
  }

  if (tools.has(name)) {
    throw new Error(`Duplicate QRE tool name: ${name}`);
  }

  tools.set(name, {
    name,
    description: definition.description ?? "Node.js QRE tool.",
    capabilities: definition.capabilities ?? [],
    timeoutSeconds: definition.timeoutSeconds ?? 30,
    maxOutputBytes: definition.maxOutputBytes ?? 200_000,
    inputSchema: definition.inputSchema ?? {
      type: "object",
      additionalProperties: true,
    },
    handler,
  });

  return handler;
}

export async function main(scriptUrl) {
  const args = process.argv.slice(2);
  const manifestDirIndex = args.indexOf("--manifest-dir");
  if (manifestDirIndex >= 0) {
    const manifestDir = args[manifestDirIndex + 1];
    if (!manifestDir) {
      console.error("--manifest-dir requires a path.");
      return 1;
    }

    const written = await writeManifests(scriptUrl, manifestDir);
    for (const path of written) {
      console.log(path);
    }
    return 0;
  }

  try {
    return await dispatch();
  } catch (error) {
    console.error(error?.stack ?? String(error));
    return 1;
  }
}

async function dispatch() {
  const request = JSON.parse(await readAllStdin());
  const name = request.name;
  const tool = tools.get(name);
  if (!tool) {
    throw new Error(`Unknown QRE tool: ${name}`);
  }

  const argumentsObject = request.arguments ?? {};
  if (argumentsObject === null || Array.isArray(argumentsObject) || typeof argumentsObject !== "object") {
    throw new Error("QRE tool arguments must be a JSON object.");
  }

  const result = await tool.handler({
    ...argumentsObject,
    workspacePath: request.workspacePath ?? ".",
  });
  process.stdout.write(`${JSON.stringify({ result })}\n`);
  return 0;
}

async function writeManifests(scriptUrl, manifestDir) {
  const scriptPath = fileURLToPath(scriptUrl);
  const target = resolve(manifestDir);
  await mkdir(target, { recursive: true });

  const written = [];
  for (const tool of tools.values()) {
    const path = resolve(target, `${tool.name}.json`);
    const manifest = {
      name: tool.name,
      description: tool.description,
      transport: "stdio",
      command: process.execPath,
      args: [scriptPath],
      capabilities: tool.capabilities,
      timeoutSeconds: tool.timeoutSeconds,
      maxOutputBytes: tool.maxOutputBytes,
      inputSchema: tool.inputSchema,
    };

    await writeFile(path, `${JSON.stringify(manifest, null, 2)}\n`, "utf8");
    written.push(path);
  }

  return written;
}

async function readAllStdin() {
  const chunks = [];
  for await (const chunk of process.stdin) {
    chunks.push(Buffer.from(chunk));
  }
  return Buffer.concat(chunks).toString("utf8");
}

export async function resolveWorkspacePath(workspacePath, relativePath = ".") {
  const root = await realpath(resolve(workspacePath));
  const target = await realpath(resolve(root, relativePath));
  if (target !== root && !target.startsWith(`${root}${process.platform === "win32" ? "\\" : "/"}`)) {
    throw new Error(`Path escapes workspace: ${relativePath}`);
  }
  return target;
}
