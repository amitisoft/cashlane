const API_BASE_URL = resolveApiBaseUrl(import.meta.env.VITE_API_BASE_URL);

function resolveApiBaseUrl(configuredBaseUrl) {
  if (!configuredBaseUrl) {
    return "/api";
  }

  const trimmedBaseUrl = configuredBaseUrl.replace(/\/+$/, "");

  if (/^https?:\/\//i.test(trimmedBaseUrl) && !trimmedBaseUrl.endsWith("/api")) {
    return `${trimmedBaseUrl}/api`;
  }

  return trimmedBaseUrl || "/api";
}

function buildUrl(path, params) {
  const url = new URL(path, window.location.origin);
  if (params) {
    Object.entries(params).forEach(([key, value]) => {
      if (value === null || value === undefined || value === "") {
        return;
      }

      url.searchParams.set(key, value);
    });
  }

  return `${API_BASE_URL}${url.pathname}${url.search}`;
}

export async function apiFetch(path, { method = "GET", token, body, params, headers } = {}) {
  const response = await fetch(buildUrl(path, params), {
    method,
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(headers || {})
    },
    body: body ? JSON.stringify(body) : undefined
  });

  if (response.status === 204) {
    return null;
  }

  const contentType = response.headers.get("content-type") || "";
  const payload = contentType.includes("application/json")
    ? await response.json()
    : await response.text();

  if (!response.ok) {
    const message =
      payload?.detail || payload?.title || payload?.message || "Request failed.";
    const error = new Error(message);
    error.status = response.status;
    error.payload = payload;
    throw error;
  }

  return payload;
}

export function downloadCsv(filename, rows) {
  const csv = rows.map((row) => row.map((cell) => `"${String(cell).replaceAll('"', '""')}"`).join(",")).join("\n");
  const blob = new Blob([csv], { type: "text/csv;charset=utf-8;" });
  const link = document.createElement("a");
  link.href = URL.createObjectURL(blob);
  link.download = filename;
  link.click();
  URL.revokeObjectURL(link.href);
}
