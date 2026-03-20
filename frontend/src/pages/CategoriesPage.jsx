import { useEffect, useMemo, useState } from "react";
import { SelectField } from "../components/SelectField";
import { useAuth } from "../lib/auth";
import { CATEGORY_TYPE_OPTIONS } from "../lib/select-options";
import { useToasts } from "../lib/toasts";

const emptyForm = {
  id: null,
  name: "",
  type: "Expense",
  color: "#1F9D74",
  icon: "circle",
  isArchived: false
};

export function CategoriesPage() {
  const { authorizedFetch } = useAuth();
  const { pushToast } = useToasts();
  const [categories, setCategories] = useState([]);
  const [form, setForm] = useState(emptyForm);

  useEffect(() => {
    authorizedFetch("/categories")
      .then(setCategories)
      .catch((error) => {
        pushToast({ kind: "danger", title: "Categories unavailable", message: error.message });
      });
  }, [authorizedFetch, pushToast]);

  const grouped = useMemo(
    () => ({
      Expense: categories.filter((item) => item.type === "Expense"),
      Income: categories.filter((item) => item.type === "Income")
    }),
    [categories]
  );

  function startEdit(category) {
    setForm({
      id: category.id,
      name: category.name,
      type: category.type,
      color: category.color,
      icon: category.icon,
      isArchived: category.isArchived
    });
  }

  async function handleSubmit(event) {
    event.preventDefault();

    try {
      const payload = {
        name: form.name,
        type: form.type,
        color: form.color,
        icon: form.icon,
        isArchived: form.isArchived
      };

      const saved = form.id
        ? await authorizedFetch(`/categories/${form.id}`, { method: "PUT", body: payload })
        : await authorizedFetch("/categories", { method: "POST", body: payload });

      setCategories((current) =>
        form.id ? current.map((item) => (item.id === saved.id ? saved : item)) : [...current, saved]
      );
      setForm(emptyForm);
      pushToast({
        kind: "success",
        title: form.id ? "Category updated" : "Category created",
        message: `${saved.name} is ready to use.`
      });
    } catch (error) {
      pushToast({ kind: "danger", title: "Save failed", message: error.message });
    }
  }

  async function archiveCategory(category) {
    try {
      await authorizedFetch(`/categories/${category.id}`, { method: "DELETE" });
      setCategories((current) =>
        current.map((item) => (item.id === category.id ? { ...item, isArchived: true } : item))
      );
      if (form.id === category.id) {
        setForm(emptyForm);
      }

      pushToast({ kind: "success", title: "Category archived", message: `${category.name} has been archived.` });
    } catch (error) {
      pushToast({ kind: "danger", title: "Archive failed", message: error.message });
    }
  }

  async function deleteCategory(category) {
    try {
      await authorizedFetch(`/categories/${category.id}/permanent`, { method: "DELETE" });
      setCategories((current) => current.filter((item) => item.id !== category.id));
      if (form.id === category.id) {
        setForm(emptyForm);
      }

      pushToast({ kind: "success", title: "Category deleted", message: `${category.name} was removed.` });
    } catch (error) {
      pushToast({ kind: "danger", title: "Delete failed", message: error.message });
    }
  }

  return (
    <div className="page-grid categories-grid">
      <section className="panel panel-wide">
        <header className="panel-header">
          <div>
            <span className="eyebrow">Categories</span>
            <h2>Income and expense classification</h2>
          </div>
        </header>
        <div className="category-columns">
          {Object.entries(grouped).map(([label, items]) => (
            <div key={label} className="category-column">
              <h3>{label}</h3>
              <div className="list-stack">
                {items.map((category) => (
                  <div key={category.id} className={`category-card ${category.isArchived ? "is-archived" : ""}`}>
                    <div className="category-card-main">
                      <span className="category-swatch" style={{ background: category.color }} />
                      <div className="category-copy">
                        <strong>{category.name}</strong>
                        {category.isArchived && <span>Archived</span>}
                      </div>
                    </div>
                    <div className="category-card-actions">
                      <button type="button" className="ghost-button" onClick={() => startEdit(category)}>
                        Edit
                      </button>
                      {category.isArchived ? (
                        <button type="button" className="ghost-button" onClick={() => deleteCategory(category)}>
                          Delete
                        </button>
                      ) : (
                        <button type="button" className="ghost-button" onClick={() => archiveCategory(category)}>
                          Archive
                        </button>
                      )}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          ))}
        </div>
      </section>

      <section className="panel">
        <header className="panel-header">
          <div>
            <span className="eyebrow">{form.id ? "Edit category" : "New category"}</span>
            <h2>{form.id ? "Update category details" : "Create a custom category"}</h2>
          </div>
        </header>
        <form className="stacked-form" onSubmit={handleSubmit}>
          <label className="field">
            <span>Name</span>
            <input value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} />
          </label>
          <label className="field">
            <span>Type</span>
            <SelectField
              ariaLabel="Category type"
              options={CATEGORY_TYPE_OPTIONS}
              value={form.type}
              onChange={(nextValue) => setForm((current) => ({ ...current, type: nextValue }))}
            />
          </label>
          <label className="field">
            <span>Color</span>
            <input value={form.color} onChange={(event) => setForm((current) => ({ ...current, color: event.target.value }))} />
          </label>
          <label className="field">
            <span>Icon label</span>
            <input value={form.icon} onChange={(event) => setForm((current) => ({ ...current, icon: event.target.value }))} />
          </label>
          <label className="field checkbox-field">
            <input
              type="checkbox"
              checked={form.isArchived}
              onChange={(event) => setForm((current) => ({ ...current, isArchived: event.target.checked }))}
            />
            <span>Archived</span>
          </label>
          <div className="inline-actions">
            <button className="primary-button" type="submit">
              {form.id ? "Save changes" : "Create category"}
            </button>
            {form.id && (
              <button type="button" className="ghost-button" onClick={() => setForm(emptyForm)}>
                Cancel edit
              </button>
            )}
          </div>
        </form>
      </section>
    </div>
  );
}
