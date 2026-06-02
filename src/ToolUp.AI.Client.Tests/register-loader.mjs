// Registers `./test-loader.mjs` as a Node ESM customisation hook.
// Invoked via Mocha's `node-option` config (`.mocharc.cjs`) so it
// runs before any test module is imported.

import { register } from "node:module";
import { pathToFileURL } from "node:url";

register("./test-loader.mjs", pathToFileURL("./"));
