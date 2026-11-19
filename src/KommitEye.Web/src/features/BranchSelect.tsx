import { useEffect, useRef, useState } from "react";
import { api, ApiError } from "../api";
import { t } from "../ui";

export function BranchSelect({
  path,
  value,
  change,
}: {
  path: string;
  value: string;
  change: (branch: string) => void;
}) {
  const [branches, setBranches] = useState<string[]>([]),
    [busy, setBusy] = useState(false),
    [trustPath, setTrustPath] = useState(""),
    [error, setError] = useState("");
  const request = useRef(0);
  async function load(trust = false) {
    const id = ++request.current;
    setBusy(true);
    setError("");
    try {
      if (trust) {
        await api("/repositories/trust", { path });
        if (id !== request.current) return;
        setTrustPath("");
      }
      const result = await api<string[]>("/repositories/branches", { path });
      if (id === request.current) {
        setBranches(result);
        setTrustPath("");
      }
    } catch (e) {
      if (id === request.current) {
        const ownership = e instanceof ApiError && e.code === "repository_ownership";
        if (ownership) setTrustPath(path);
        else if (!trust) setTrustPath("");
        setError(ownership ? e.message.split("To add an exception")[0].trim() : (e as Error).message);
      }
    } finally {
      if (id === request.current) setBusy(false);
    }
  }
  useEffect(() => {
    ++request.current;
    setBranches([]);
    setTrustPath("");
    setError("");
    setBusy(false);
    if (!path.trim()) return;
    const timer = window.setTimeout(() => void load(), 500);
    return () => {
      clearTimeout(timer);
      ++request.current;
    };
  }, [path]);
  const selected = branches.includes(value)
    ? value
    : branches.includes("refs/heads/" + value)
      ? "refs/heads/" + value
      : value;
  return (
    <div>
      <div className="path-field">
        <label className="field">
          <span>{t("Ветка", "Branch")}</span>
          <select
            value={selected}
            onChange={(e) => change(e.target.value)}
            disabled={!path.trim() || busy}
          >
            {!branches.includes(selected) && (
              <option value={selected}>
                {selected || t("Выберите ветку", "Select a branch")}
              </option>
            )}
            {branches.map((branch) => (
              <option key={branch} value={branch}>
                {branch.replace(/^refs\/heads\//, "")}
              </option>
            ))}
          </select>
        </label>
        <button disabled={!path.trim() || busy} onClick={() => void load()}>
          {busy
            ? t("Загрузка…", "Loading…")
            : t("Загрузить ветки", "Load branches")}
        </button>
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {trustPath && trustPath === path && (
        <div>
          <p>{t("Разрешить Git учётной записи сервера работать с этой папкой?", "Allow Git for the server account to use this folder?")} <code>{path}</code></p>
          <button type="button" disabled={busy} onClick={() => void load(true)}>
            {t("Доверять этому репозиторию", "Trust this repository")}
          </button>
        </div>
      )}
      {!path.trim() && (
        <p className="muted">
          {t(
            "Сначала выберите папку репозитория.",
            "Select a repository folder first.",
          )}
        </p>
      )}
    </div>
  );
}
