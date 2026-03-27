import { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { REPORT_TRANSACTION_TYPE_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";
import { formatCurrency } from "../components/charts";

export function TransactionsPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [searchParams, setSearchParams] = useSearchParams();
  const [data, setData] = useState(null);
  const [accounts, setAccounts] = useState([]);
  const [categories, setCategories] = useState([]);
  const [importPreview, setImportPreview] = useState(null);
  const [importContent, setImportContent] = useState("");
  const [importName, setImportName] = useState("");

  const filters = useMemo(
    () => ({
      search: searchParams.get("q") || "",
      accountId: searchParams.get("accountId") || "",
      categoryId: searchParams.get("categoryId") || "",
      type: searchParams.get("type") || "",
      dateFrom: searchParams.get("dateFrom") || "",
      dateTo: searchParams.get("dateTo") || "",
      minAmount: searchParams.get("minAmount") || "",
      maxAmount: searchParams.get("maxAmount") || "",
      page: Number(searchParams.get("page") || 1),
      pageSize: Number(searchParams.get("pageSize") || 10)
    }),
    [searchParams]
  );

  const selectedAccount = useMemo(
    () => accounts.find((account) => account.id === filters.accountId) || null,
    [accounts, filters.accountId]
  );

  const accountOptions = [{ value: "", label: "All accounts" }, ...accounts.map((account) => ({ value: account.id, label: account.name }))];
  const categoryOptions = [{ value: "", label: "All categories" }, ...categories.map((category) => ({ value: category.id, label: category.name }))];
  const pageSizeOptions = [10, 20, 50].map((size) => ({ value: size, label: `${size} / page` }));

  useEffect(() => {
    Promise.all([
      authorizedFetch("/transactions", {
        params: {
          search: filters.search,
          accountId: filters.accountId,
          categoryId: filters.categoryId,
          type: filters.type,
          dateFrom: filters.dateFrom,
          dateTo: filters.dateTo,
          minAmount: filters.minAmount,
          maxAmount: filters.maxAmount,
          page: filters.page,
          pageSize: filters.pageSize
        }
      }),
      authorizedFetch("/accounts"),
      authorizedFetch("/categories", { params: { accountId: selectedAccount?.isShared ? selectedAccount.id : "" } })
    ])
      .then(([transactionData, accountData, categoryData]) => {
        setData(transactionData);
        setAccounts(accountData);
        setCategories(categoryData);
      })
      .catch((error) => {
        pushToast({ kind: "danger", title: "Transactions unavailable", message: error.message });
      });
  }, [authorizedFetch, filters, pushToast, selectedAccount?.id, selectedAccount?.isShared]);

  function updateFilter(next) {
    const merged = { ...filters, ...next };
    const params = new URLSearchParams();
    Object.entries(merged).forEach(([key, value]) => {
      if (value === "" || value === null || value === undefined) {
        return;
      }

      params.set(key === "search" ? "q" : key, String(value));
    });
    setSearchParams(params);
  }

  async function handleDelete(id) {
    try {
      await authorizedFetch(`/transactions/${id}`, { method: "DELETE" });
      setData((current) => ({
        ...current,
        items: current.items.filter((item) => item.id !== id),
        totalCount: Math.max((current.totalCount || 1) - 1, 0)
      }));
      pushToast({ kind: "success", title: "Deleted", message: "Transaction removed." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Delete failed", message: error.message });
    }
  }

  async function loadImportFile(event) {
    const file = event.target.files?.[0];
    if (!file) {
      return;
    }

    const content = await file.text();
    setImportName(file.name);
    setImportContent(content);
    setImportPreview(null);
  }

  async function previewImport() {
    try {
      const preview = await authorizedFetch("/transactions/import/preview", {
        method: "POST",
        body: { csvContent: importContent }
      });
      setImportPreview(preview);
      pushToast({ kind: "success", title: "Preview ready", message: "Review the rows before importing." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Preview failed", message: error.message });
    }
  }

  async function commitImport() {
    try {
      await authorizedFetch("/transactions/import/commit", {
        method: "POST",
        body: { csvContent: importContent }
      });
      setImportPreview(null);
      setImportContent("");
      setImportName("");
      const refreshed = await authorizedFetch("/transactions", { params: { ...filters } });
      setData(refreshed);
      pushToast({ kind: "success", title: "Import complete", message: "Transactions imported successfully." });
    } catch (error) {
      pushToast({ kind: "danger", title: "Import failed", message: error.message });
    }
  }

  const pageCount = data ? Math.max(1, Math.ceil(data.totalCount / data.pageSize)) : 1;

  return (
    <div className="page-grid">
      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Import</span>
            <h2>Cashlane CSV import</h2>
          </div>
        </header>
        <div className="stacked-form">
          <input type="file" accept=".csv,text/csv" onChange={loadImportFile} />
          {importName && <span className="panel-empty">{importName}</span>}
          <div className="inline-actions">
            <button type="button" className="ghost-button" onClick={previewImport} disabled={!importContent}>
              Preview import
            </button>
            <button type="button" className="primary-button" onClick={commitImport} disabled={!importPreview?.canCommit}>
              Commit import
            </button>
          </div>
          {importPreview && (
            <div className="list-stack">
              {importPreview.rows.slice(0, 8).map((row) => (
                <article key={row.rowNumber} className={`detail-card ${row.status === "Valid" ? "" : "is-invalid"}`}>
                  <div className="metric-row">
                    <strong>Row {row.rowNumber}</strong>
                    <span>{row.status}</span>
                  </div>
                  <p>
                    {row.transactionDate} · {row.accountName} · {row.categoryName} · {row.merchant || "No merchant"}
                  </p>
                  {row.validationErrors.length > 0 && <p>{row.validationErrors.join(" ")}</p>}
                  {row.alerts.length > 0 && <p>{row.alerts.join(", ")}</p>}
                </article>
              ))}
              {importPreview.rows.length > 8 && <p className="panel-empty">Showing the first 8 preview rows.</p>}
            </div>
          )}
        </div>
      </section>

      <section className="panel panel-wide transactions-panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Search</span>
            <h2>Transactions</h2>
          </div>
        </header>

        <div className="filters-grid">
          <input placeholder="Search merchant or note" value={filters.search} onChange={(event) => updateFilter({ search: event.target.value, page: 1 })} />
          <SelectField ariaLabel="Transactions account filter" options={accountOptions} value={filters.accountId} onChange={(nextValue) => updateFilter({ accountId: nextValue, categoryId: "", page: 1 })} />
          <SelectField ariaLabel="Transactions category filter" options={categoryOptions} value={filters.categoryId} onChange={(nextValue) => updateFilter({ categoryId: nextValue, page: 1 })} />
          <SelectField ariaLabel="Transactions type filter" options={REPORT_TRANSACTION_TYPE_OPTIONS} value={filters.type} onChange={(nextValue) => updateFilter({ type: nextValue, page: 1 })} />
          <input type="date" value={filters.dateFrom} onChange={(event) => updateFilter({ dateFrom: event.target.value, page: 1 })} />
          <input type="date" value={filters.dateTo} onChange={(event) => updateFilter({ dateTo: event.target.value, page: 1 })} />
          <input type="number" placeholder="Min amount" value={filters.minAmount} onChange={(event) => updateFilter({ minAmount: event.target.value, page: 1 })} />
          <input type="number" placeholder="Max amount" value={filters.maxAmount} onChange={(event) => updateFilter({ maxAmount: event.target.value, page: 1 })} />
        </div>

        <div className="table-shell">
          <div className="table-head">
            <span>Date</span>
            <span>Merchant</span>
            <span>Category</span>
            <span>Account</span>
            <span>Type</span>
            <span>Amount</span>
            <span>Actions</span>
          </div>
          {data?.items?.map((item) => (
            <div key={item.id} className="table-row">
              <span>{item.transactionDate}</span>
              <span>{item.merchant || "Transfer"}</span>
              <span>{item.categoryName || "Transfer"}</span>
              <span>{item.accountName}</span>
              <span>{item.type}</span>
              <strong>{formatCurrency(item.amount)}</strong>
              <div className="inline-actions">
                <button className="ghost-button" type="button" onClick={() => handleDelete(item.id)}>
                  Delete
                </button>
              </div>
            </div>
          ))}
        </div>

        <div className="pagination-row">
          <span>
            Page {filters.page} of {pageCount}
          </span>
          <div className="inline-actions">
            <SelectField ariaLabel="Transactions page size" options={pageSizeOptions} value={filters.pageSize} onChange={(nextValue) => updateFilter({ pageSize: nextValue, page: 1 })} />
            <button type="button" className="ghost-button" onClick={() => updateFilter({ page: Math.max(filters.page - 1, 1) })} disabled={filters.page <= 1}>
              Previous
            </button>
            <button type="button" className="ghost-button" onClick={() => updateFilter({ page: Math.min(filters.page + 1, pageCount) })} disabled={filters.page >= pageCount}>
              Next
            </button>
          </div>
        </div>
      </section>
    </div>
  );
}
