import { useEffect, useState } from "react";
import { api } from "../api";
import { Heading, language, Modal, t } from "../ui";

type Failure = {
  id: number; savedAt: string; projectName: string; branch: string; commitSha: string; error: string;
};
type Page = { items: Failure[]; hasMore: boolean };
type Detail = {
  failure: Failure; projectId: string; repository: string; model: string; manual: boolean;
  logs: { time: string; source: string; text: string }[];
};

function date(value: string) {
  return new Date(value).toLocaleString(language);
}

function FailureDetails({ id, close }: { id: string; close: () => void }) {
  const [detail, setDetail] = useState<Detail | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let active = true;
    api<Detail>(`/diagnostics/${id}`)
      .then(value => { if (active) setDetail(value); })
      .catch(e => { if (active) setError(e.message === "HTTP 404" ? t("Запись не найдена.", "Record not found.") : e.message); });
    return () => { active = false; };
  }, [id]);
  return <Modal titleId="diagnostic-title" onClose={close}>
    <div className="diagnostic-detail">
      <div className="section-heading">
        <h2 id="diagnostic-title">{t("Диагностика", "Diagnostic")} #{id}</h2>
        <button onClick={close}>{t("Закрыть", "Close")}</button>
      </div>
      {error ? <p className="error" role="alert">{error}</p> : !detail ? <p role="status">{t("Загрузка…", "Loading…")}</p> : <>
        <p>{date(detail.failure.savedAt)} · {detail.manual ? t("Ручная проверка", "Manual review") : t("Автопроверка", "Automatic review")}</p>
        <h3>{t("Причина остановки", "Failure reason")}</h3>
        <pre className="log">{detail.failure.error}</pre>
        <dl>
          <dt>{t("Проект", "Project")}</dt><dd>{detail.failure.projectName || detail.projectId || "—"}</dd>
          <dt>{t("Репозиторий", "Repository")}</dt><dd>{detail.repository || "—"}</dd>
          <dt>{t("Ветка", "Branch")}</dt><dd>{detail.failure.branch || "—"}</dd>
          <dt>{t("Коммит", "Commit")}</dt><dd className="mono">{detail.failure.commitSha || "—"}</dd>
          <dt>{t("Модель", "Model")}</dt><dd>{detail.model || "—"}</dd>
        </dl>
        <h3>{t("Последние события", "Last events")}</h3>
        {detail.logs.length ? <pre className="log" tabIndex={0}>{detail.logs.map(line =>
          `${date(line.time)} | ${line.source} | ${line.text}`).join("\n")}</pre> : <p>{t("Нет сохранённых событий.", "No events saved.")}</p>}
      </>}
    </div>
  </Modal>;
}

export function Diagnostics() {
  const [history, setHistory] = useState<number[]>([]);
  const before = history.at(-1);
  const [page, setPage] = useState<Page | null>(null);
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [number, setNumber] = useState("");
  const [selected, setSelected] = useState(() => {
    const id = new URLSearchParams(location.search).get("diagnostic") || "";
    return /^[1-9]\d*$/.test(id) ? id : "";
  });
  useEffect(() => {
    let active = true;
    setPage(null); setError("");
    api<Page>(before ? `/diagnostics?before=${before}` : "/diagnostics")
      .then(value => { if (active) setPage(value); })
      .catch(e => { if (active) setError(e.message); });
    return () => { active = false; };
  }, [before, refresh]);
  return <>
    <Heading title={t("Диагностика", "Diagnostics")} description={t("Сохранённые ошибки проверок и события перед остановкой.", "Saved review failures and the events leading up to them.")} />
    <form className="buttons" onSubmit={e => { e.preventDefault(); setSelected(number); }}>
      <label className="field">{t("Номер из журнала", "Journal record number")}
        <input inputMode="numeric" pattern="[1-9][0-9]*" required value={number} onChange={e => setNumber(e.target.value)} />
      </label>
      <button type="submit">{t("Открыть", "Open")}</button>
      <button type="button" onClick={() => { setHistory([]); setRefresh(n => n + 1); }}>{t("Обновить", "Refresh")}</button>
    </form>
    {error ? <p className="error" role="alert">{error}</p> : !page ? <p role="status">{t("Загрузка…", "Loading…")}</p> : !page.items.length ?
      <p className="empty">{t("Сохранённых ошибок пока нет.", "No failures recorded yet.")}</p> :
      <div className="table-wrap"><table><thead><tr>
        <th>{t("Запись", "Record")}</th><th>{t("Проект / коммит", "Project / commit")}</th><th>{t("Ошибка", "Error")}</th>
      </tr></thead><tbody>{page.items.map(row => <tr key={row.id}>
        <td><button className="text-button" onClick={() => setSelected(String(row.id))}>#{row.id}</button><small>{date(row.savedAt)}</small></td>
        <td>{row.projectName || "—"}<small>{row.branch} {row.commitSha.slice(0, 8)}</small></td>
        <td>{row.error}</td>
      </tr>)}</tbody></table></div>}
    {(history.length > 0 || page?.hasMore) && <div className="buttons">
      <button disabled={!history.length} onClick={() => setHistory(p => p.slice(0, -1))}>{t("Новее", "Newer")}</button>
      <button disabled={!page?.hasMore} onClick={() => { if (page?.hasMore) setHistory(p => [...p, page.items[page.items.length - 1].id]); }}>{t("Старее", "Older")}</button>
    </div>}
    {selected && <FailureDetails key={selected} id={selected} close={() => {
      setSelected("");
      if (new URLSearchParams(location.search).has("diagnostic")) window.history.replaceState(null, "", location.pathname);
    }} />}
  </>;
}
