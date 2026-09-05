#!/usr/bin/env node
// Example Node.js functions exposed to QRE as external tools.

import { readdir, readFile } from "node:fs/promises";
import { extname, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { main, qreTool, resolveWorkspacePath } from "./qre_node_tool.mjs";

qreTool(
  {
    name: "node_count_files",
    description: "Count files under the workspace with an optional extension filter.",
    capabilities: ["read_fs"],
    inputSchema: {
      type: "object",
      properties: {
        extension: {
          type: "string",
          description: "Optional file extension filter, such as .js or .md.",
          default: ".js",
        },
        max_files: {
          type: "integer",
          description: "Maximum number of files to inspect.",
          minimum: 1,
          maximum: 5000,
          default: 1000,
        },
      },
      additionalProperties: false,
    },
  },
  async (args) => {
    const { workspacePath, extension = ".js" } = args;
    const maxFiles = Number(args.maxFiles ?? args.max_files ?? 1000);
    if (!Number.isInteger(maxFiles) || maxFiles < 1 || maxFiles > 5000) {
      throw new Error("max_files must be an integer between 1 and 5000");
    }
    const root = resolve(workspacePath);
    const normalizedExtension = extension ? (extension.startsWith(".") ? extension : `.${extension}`) : "";
    const files = [];
    for await (const path of walkFiles(root)) {
      if (!normalizedExtension || extname(path) === normalizedExtension) {
        files.push(relative(root, path).split("\\").join("/"));
        if (files.length >= maxFiles) {
          break;
        }
      }
    }

    return {
      extension: normalizedExtension,
      count: files.length,
      sample: files.slice(0, 10),
    };
  },
);

qreTool(
  {
    name: "node_read_text_file",
    description: "Read a UTF-8 text file from the workspace.",
    capabilities: ["read_fs"],
    inputSchema: {
      type: "object",
      properties: {
        path: {
          type: "string",
          description: "Workspace-relative file path.",
        },
        max_chars: {
          type: "integer",
          description: "Maximum number of UTF-8 characters to return.",
          minimum: 1,
          maximum: 20000,
          default: 4000,
        },
      },
      required: ["path"],
      additionalProperties: false,
    },
  },
  async (args) => {
    const { workspacePath, path } = args;
    const maxChars = Number(args.maxChars ?? args.max_chars ?? 4000);
    if (!Number.isInteger(maxChars) || maxChars < 1 || maxChars > 20000) {
      throw new Error("max_chars must be an integer between 1 and 20000");
    }
    const target = await resolveWorkspacePath(workspacePath, path);
    const text = await readFile(target, "utf8");
    return {
      path,
      chars: Math.min(text.length, maxChars),
      text: text.slice(0, maxChars),
    };
  },
);

async function* walkFiles(root) {
  const pending = [root];
  while (pending.length > 0) {
    const directory = pending.pop();
    let entries;
    try {
      entries = await readdir(directory, { withFileTypes: true });
    } catch {
      continue;
    }

    entries.sort((left, right) => left.name.localeCompare(right.name));
    for (const entry of entries) {
      if (shouldSkip(entry.name)) {
        continue;
      }

      const path = resolve(directory, entry.name);
      if (entry.isDirectory()) {
        pending.push(path);
        continue;
      }

      if (entry.isFile()) {
        yield path;
        continue;
      }

      // Skip symbolic links and special files.
    }
  }
}

function shouldSkip(name) {
  return name === ".git" || name === ".qre" || name === "node_modules" || name === "bin" || name === "obj";
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  process.exitCode = await main(import.meta.url);
}
