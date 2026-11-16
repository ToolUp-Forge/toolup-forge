import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import svgr from "vite-plugin-svgr";
// Resolves `@nuget/<package-id>/...` in index.css to the restored package.
import nugetContent from "../../client-styling/vite-fuaran-content.mjs";

// Port is env-driven with no default. The Fable acceptance gate
// (`dotnet fable -o output --noCache`) doesn't run Vite, so no port
// is claimed by ship. If an operator wants to `npm run dev` this
// sample, they pass VITE_DEV_PORT explicitly.
const clientPort = process.env.VITE_DEV_PORT
    ? parseInt(process.env.VITE_DEV_PORT)
    : undefined;

export default defineConfig(() => ({
    plugins: [
        nugetContent(),
        // svgr must run BEFORE react so SDK Client files' `./icons/foo.svg?react`
        // imports resolve to React components rather than data: URLs.
        svgr({ svgrOptions: { dimensions: false } }),
        react(),
    ],
    server: clientPort ? { port: clientPort } : undefined,
}));
