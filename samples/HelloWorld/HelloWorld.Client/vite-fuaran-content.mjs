// >>> @toolup/client-styling canonical vite plugin — DO NOT EDIT, copied verbatim <<<
// Canonical companion to the client CSS entry. Consumers copy this file into
// their client dir (next to vite.config.mts) and add `nugetContent()` to the
// Vite `plugins` array. check-drift.ps1 fails if a consumer's copy differs
// from this canonical source.
//
// Why: a NuGet package ships files under its `content/` folder (the Fuaran-UI
// renderer ships `fuaran-reference.css` there), but restore leaves them in the
// global packages folder, at a path that names the machine's user and the
// package version. A CSS entry cannot name that path. This plugin reads the
// client project's restore record (`obj/project.assets.json`, which restore
// writes before Fable or Vite runs) and maps
//
//     @nuget/<package-id>/<path inside the package>
//
// to the folder restore resolved for that package, at the version restore
// chose. So `@import "@nuget/fuaran.ui.renderer/content/fuaran-reference.css"`
// always reads the stylesheet of the renderer the client was compiled against.
// The package id is written in lower case, as NuGet stores it on disk.
//
// The mapping is a Vite `resolve.alias`, which both Vite's own CSS `@import`
// and @tailwindcss/vite honour.
import fs from "node:fs";
import path from "node:path";

const PREFIX = "@nuget/";

function restoredPackages(root, assetsFile) {
  const file = path.resolve(root, assetsFile);
  if (!fs.existsSync(file)) {
    throw new Error(
      `nugetContent: ${file} does not exist. Restore the client project (dotnet restore) before running Vite.`,
    );
  }
  const assets = JSON.parse(fs.readFileSync(file, "utf8"));
  const folders = Object.keys(assets.packageFolders ?? {});
  const packages = new Map();
  for (const [key, library] of Object.entries(assets.libraries ?? {})) {
    if (library.type !== "package" || !library.path) continue;
    const id = key.slice(0, key.indexOf("/")).toLowerCase();
    const folder = folders.map((f) => path.join(f, library.path)).find((p) => fs.existsSync(p));
    if (folder) packages.set(id, folder);
  }
  return packages;
}

export default function nugetContent(options = {}) {
  const assetsFile = options.assetsFile ?? path.join("obj", "project.assets.json");
  return {
    name: "nuget-content",
    enforce: "pre",
    config(config) {
      const root = path.resolve(config.root ?? process.cwd());
      const packages = restoredPackages(root, assetsFile);
      // A string alias matches the id itself or the id followed by `/`, so
      // `@nuget/fuaran.ui` never captures `@nuget/fuaran.ui.renderer/...`.
      const aliases = [...packages].map(([id, folder]) => ({
        find: PREFIX + id,
        replacement: folder.split(path.sep).join("/").replace(/\/$/, ""),
      }));
      return { resolve: { alias: aliases } };
    },
  };
}
// >>> end @toolup/client-styling canonical vite plugin <<<
