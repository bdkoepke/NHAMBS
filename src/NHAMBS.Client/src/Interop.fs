module NHAMBS.Client.Interop

open Fable.Core
open Fable.Core.JsInterop

[<Import("createGrid", "ag-grid-community")>]
let createGrid (element: obj) (options: obj) : obj = jsNative

[<Emit("$0.destroy()")>]
let destroyGrid (api: obj) : unit = jsNative

[<Emit("$0.setGridOption('rowData', $1)")>]
let setGridRowData (api: obj) (rows: obj) : unit = jsNative

[<Emit("$0.setColumnsVisible($1, $2)")>]
let setColumnsVisible (api: obj) (colIds: string[]) (visible: bool) : unit = jsNative

[<Emit("$0.setGridOption('quickFilterText', $1)")>]
let setQuickFilter (api: obj) (text: string) : unit = jsNative

[<Emit("$0.setFilterModel({})")>]
let clearFilters (api: obj) : unit = jsNative

[<Emit("$0.getColumnState().filter((column) => column.sort).map((column) => ({ colId: column.colId, sort: column.sort }))")>]
let getSortModel (api: obj) : obj = jsNative

[<Emit("setTimeout($0, 50)")>]
let defer (action: unit -> unit) : unit = jsNative

[<Emit("$0.getFilterModel()")>]
let getFilterModel (api: obj) : obj = jsNative

[<Emit("$0.applyColumnState({ state: $1 || [], defaultState: { sort: null } })")>]
let setSortModel (api: obj) (model: obj) : unit = jsNative

[<Emit("$0.setGridOption('filterModel', $1)")>]
let setFilterModel (api: obj) (model: obj) : unit = jsNative

[<Emit("Object.entries($0 || {}).map(([colId, filter]) => ({ colId, filter }))")>]
let filterEntries (model: obj) : obj[] = jsNative

[<Emit("""(() => {
  const api = $0;
  const root = $1;
  const fields = new Set($2);
  const grips = window.__nhambsHeaderGrips || (window.__nhambsHeaderGrips = new WeakMap());

  const sync = () => {
    root.querySelectorAll('.ag-header-cell').forEach((cell) => {
      const colId = cell.getAttribute('col-id');
      const label = cell.querySelector('.ag-header-cell-label');
      const existing = label ? label.querySelector('.header-group-grip') : null;
      const shouldHave = colId != null && fields.has(colId);

      if (shouldHave && !existing && label) {
        const grip = document.createElement('span');
        grip.className = 'header-group-grip';
        grip.textContent = '⠿';
        grip.title = 'Drag to group by this column';
        grip.draggable = true;
        grip.addEventListener('mousedown', (event) => event.stopPropagation());
        grip.addEventListener('click', (event) => event.stopPropagation());
        grip.addEventListener('dragstart', (event) => {
          event.stopPropagation();
          const currentColId = cell.getAttribute('col-id');
          if (!currentColId) return;
          event.dataTransfer.setData('text/plain', currentColId);
          event.dataTransfer.effectAllowed = 'copy';
        });
        label.appendChild(grip);
      } else if (!shouldHave && existing) {
        existing.remove();
      }
    });
  };

  const observer = new MutationObserver(sync);
  observer.observe(root, { childList: true, subtree: true });
  sync();

  return () => {
    observer.disconnect();
    root.querySelectorAll('.header-group-grip').forEach((grip) => grip.remove());
    grips.delete(api);
  };
})()""")>]
let attachHeaderGrips (api: obj) (root: obj) (fields: string[]) : unit -> unit = jsNative

[<Emit("""((column) => {
  const field = column.field;
  return Object.assign({}, column, {
    sortable: true,
    filter: true,
    comparator: () => 0,
    filterValueGetter: (params) => {
      if (!field) return null;
      const data = params.data;
      if (!data) return null;
      if (data.__filterValues && data.__filterValues[field] !== undefined) return data.__filterValues[field];
      return data[field];
    }
  });
})($0)""")>]
let groupedColumn (column: obj) : obj = jsNative

[<Emit("$0.setGridOption('paginationPageSize', $1)")>]
let setPaginationPageSize (api: obj) (size: int) : unit = jsNative

[<Emit("Object.assign({}, $0, { sortable: false, filter: false })")>]
let withoutColumnInteraction (column: obj) : obj = jsNative

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
