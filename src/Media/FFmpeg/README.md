# ToolUp.Media.FFmpeg

Local-ffmpeg transcode sub-companion for
[`ToolUp.MediaLibrary`](../MediaLibrary/README.md) (Phase 88): executes
the media library's poster-frame and HLS-rendition transcode hooks
against an operator-provided `ffmpeg` binary. Opt-in — deployments
without transcode needs (or that use the cloud sub-companion) never
reference it.

See
[`docs/companions/media-library.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/companions/media-library.md).
