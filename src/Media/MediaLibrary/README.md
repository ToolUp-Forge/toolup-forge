# ToolUp.MediaLibrary

Time-based media companion for ToolUp (Phase 88): HTTP range-streamed
video/audio (`206 Partial Content`), scope-signed expiring URLs, and
poster / HLS transcode hooks behind the `IMediaLibrary` seam.
`ServerConfig.MediaLibrary = NoMediaLibrary` (the default) strips the
surface byte-for-byte — a deployment adopts only to host time-based
media.

Transcode execution is delegated to sub-companions:
[`ToolUp.Media.FFmpeg`](../FFmpeg/README.md) (local ffmpeg) and
[`ToolUp.Media.CloudTranscode`](../CloudTranscode/README.md) (managed
cloud jobs).

See
[`docs/companions/media-library.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/companions/media-library.md).
