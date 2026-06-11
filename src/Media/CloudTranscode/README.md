# ToolUp.Media.CloudTranscode

Managed-cloud transcode sub-companion for
[`ToolUp.MediaLibrary`](../MediaLibrary/README.md) (Phase 88): runs the
media library's poster-frame and HLS-rendition transcode hooks as
managed cloud transcode jobs instead of a local ffmpeg process. Opt-in
— deployments without transcode needs (or that use the local FFmpeg
sub-companion) never reference it.

See
[`docs/companions/media-library.md`](https://github.com/ToolUp-Forge/toolup-forge/blob/main/docs/companions/media-library.md).
