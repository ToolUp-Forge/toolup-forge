// >>> @toolup/tailwind canonical vite plugin — DO NOT EDIT, copied verbatim <<<
// Canonical companion to index.css. Consumers copy this file into their
// client dir (next to vite.config.mts) and add `fableTailwindGitignore()`
// to the Vite `plugins` array BEFORE `tailwindcss()`. check-drift.ps1
// fails if a consumer's copy differs from this canonical source.
//
// Why: the SDK shell's Tailwind classes (bg-sidebar, bg-brand, …) exist
// only in Fable's compiled output (the shell ships in the
// ToolUp.Platform.Client NuGet pkg, not the app source). The canonical
// index.css carries `@source "./output"` which overrides the REPO-level
// `output/` .gitignore — but Fable also writes
// `output/fable_modules/.gitignore` (content `**/*`), a NESTED ignore
// `@source` cannot override, and `@tailwindcss/oxide`'s scan honours it.
// Fable REWRITES that nested file on every (re)compile, so a one-shot
// build step cannot hold in `dotnet fable watch` dev. This plugin empties
// it at build start AND re-empties it whenever Fable rewrites it during a
// watch session — covering `vite` (dev) and `vite build` with no SDK
// repack. `enforce: "pre"` so it runs before @tailwindcss/vite scans.
import fs from "node:fs";
import path from "node:path";

export default function fableTailwindGitignore() {
  let target;
  const empty = () => {
    try {
      if (target && fs.existsSync(target) && fs.readFileSync(target, "utf8") !== "") {
        fs.writeFileSync(target, "");
      }
    } catch {
      /* best-effort; a transient FS race self-heals on the next compile */
    }
  };
  return {
    name: "fable-tailwind-gitignore",
    enforce: "pre",
    configResolved(config) {
      target = path.join(config.root, "output", "fable_modules", ".gitignore");
      empty();
    },
    buildStart() {
      empty();
    },
    configureServer(server) {
      empty();
      // Fable-watch recreates the nested .gitignore on every recompile;
      // re-empty whenever it reappears/changes so subsequent oxide scans
      // (HMR / full reload) keep seeing the SDK shell classes.
      server.watcher.add(target);
      const onFs = (p) => {
        if (path.resolve(p) === path.resolve(target)) empty();
      };
      server.watcher.on("add", onFs);
      server.watcher.on("change", onFs);
    },
  };
}
// >>> end @toolup/tailwind canonical vite plugin <<<
