export async function api<T = any>(path: string, body?: unknown): Promise<T> {
  const response = await fetch("/api/v1" + path, {
    credentials: "same-origin",
    method: body === undefined ? "GET" : "POST",
    headers: {
      "Content-Type": "application/json",
      "X-KommitEye-Client": "web",
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const result = await response.json().catch(() => ({}));
    if (response.status === 401)
      window.dispatchEvent(new Event("session-expired"));
    throw new Error(
      result.error ||
        (response.status === 401
          ? "Войдите в систему / Sign in required"
          : `HTTP ${response.status}`),
    );
  }
  if (response.status === 204 || response.headers.get("content-length") === "0")
    return undefined as T;
  return response.headers.get("content-type")?.includes("application/json")
    ? response.json()
    : (response.text() as Promise<T>);
}
