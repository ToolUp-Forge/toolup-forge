// Ambient context for `src/ToolUp.Platform/technical-guide/09-module-conventions-data-flow-and-build.md`.
//
// The failure-routing shim is lifted out of `SessionFileStore.AddFile`,
// where every name it reads is already in scope: the upload being saved,
// the processed result and its summary entry, the resolved scope, the
// uploading user, and the per-compose `FileManagementRuntime` the hooks
// pipeline reads from.
open ProcessedDataTypes

[<AutoOpen>]
module PageAmbient =

    let upload: DataFileUpload = failwith "ambient"

    let data: ProcessedData = failwith "ambient"

    let entry: ProcessedFileEntry = failwith "ambient"

    let scope: StorageScope = failwith "ambient"

    let createdBy: string = failwith "ambient"

    let runtime: ToolUp.Platform.FileManagement.FileManagementRuntime =
        failwith "ambient"