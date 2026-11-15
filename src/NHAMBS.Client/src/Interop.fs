module NHAMBS.Client.Interop

open Fable.Core
open Fable.Core.JsInterop

[<Import("createGrid", "ag-grid-community")>]
let createGrid (element: obj) (options: obj) : obj = jsNative

[<Emit("$0.destroy()")>]
let destroyGrid (api: obj) : unit = jsNative

[<Emit("$0.setGridOption('rowData', $1)")>]
let setGridRowData (api: obj) (rows: obj) : unit = jsNative

[<Emit("$0.setGridOption('paginationPageSize', $1)")>]
let setPaginationPageSize (api: obj) (size: int) : unit = jsNative

[<Emit("window.location.hash = $0")>]
let setHash (hash: string) : unit = jsNative

[<Emit("window.scrollTo({ top: 0, behavior: 'smooth' })")>]
let scrollToTop () : unit = jsNative

[<Emit("window.open($0, '_blank', 'noopener')")>]
let openUrl (url: string) : unit = jsNative

[<Emit("new Blob([$0], { type: 'text/csv;charset=utf-8;' })")>]
let createCsvBlob (content: string) : obj = jsNative

[<Emit("""(() => { const url = URL.createObjectURL($0); const a = document.createElement('a'); a.href = url; a.download = $1; document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url); })()""")>]
let downloadBlob (blob: obj) (fileName: string) : unit = jsNative
