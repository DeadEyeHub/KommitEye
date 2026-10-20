import { useEffect, useState } from "react";
import { api } from "../api";
import { t, Heading } from "../ui";

type Entry = {
  id: number; reportName: string; repository: string; branch: string; sha: string;
  shortSha: string; subject: string; author: string; commitDate: string | null;
  savedAt: string; legacy: boolean; outcome: string;
};
type Page = { items: Entry[]; total: number; offset: number; limit: number };
type Group = { name: string };
const pageSize = 50;
const outcomeLabel = (value: string) => ({
  bug: t("Баг", "Bug"), clean: t("Не баг", "No bug"), failed: t("Ошибка проверки", "Review failed"),
  empty: t("Нет изменений", "Empty diff"), unknown: t("Не определено", "Unknown"),
}[value] || t("Не определено", "Unknown"));

export function Reports({ tick }: { tick: number }) {
  const [query, setQuery] = useState("");
  const [search, setSearch] = useState("");
  const [sort, setSort] = useState("saved");
  const [descending, setDescending] = useState(true);
  const [report, setReport] = useState("");
  const [outcome, setOutcome] = useState("");
  const [offset, setOffset] = useState(0);
  const [groups, setGroups] = useState<Group[]>([]);
  const [page, setPage] = useState<Page>({ items: [], total: 0, offset: 0, limit: pageSize });
  const [error, setError] = useState("");
  const [loading, setLoading] = useState(true);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const timer = setTimeout(() => { setSearch(query.trim()); setOffset(0); }, 300);
    return () => clearTimeout(timer);
  }, [query]);
  useEffect(() => {
    let current = true;
    setLoading(true); setError("");
    const parameters = new URLSearchParams({ q: search, sort, descending: String(descending), offset: String(offset), limit: String(pageSize) });
    if (report) parameters.set("report", report);
    if (outcome) parameters.set("outcome", outcome);
    Promise.all([api<Page>("/report-entries?" + parameters), api<Group[]>("/reports")])
      .then(([result, reports]) => {
        if (!current) return;
        setGroups(reports);
        if (report && !reports.some(r => r.name === report)) { setReport(""); setOffset(0); return; }
        if (offset > 0 && offset >= result.total) { setOffset(0); return; }
        setPage(result);
      })
      .catch(e => { if (current) setError(e.message); })
      .finally(() => { if (current) setLoading(false); });
    return () => { current = false; };
  }, [tick, search, sort, descending, offset, report, outcome, refresh]);
  const changeSort = (value: string) => { setSort(value); setOffset(0); };
  const branchUrl = "/api/v1/reports/" + encodeURIComponent(report);
  return <>
    <Heading title={t("Отчёты", "Reports")} description={t("Одна запись на коммит. Найдите проверку по автору, SHA или тексту замечания.", "One entry per commit. Find a review by author, SHA or finding text.")} />
    <div className="report-controls">
      <label className="report-search">{t("Поиск по отчётам", "Search reports")}
        <input type="search" value={query} maxLength={500} placeholder={t("SHA, автор, тема, ветка или текст…", "SHA, author, subject, branch or text…")} onChange={e => setQuery(e.target.value)} />
      </label>
      <label>{t("Репозиторий / ветка", "Repository / branch")}
        <select value={report} onChange={e => { setReport(e.target.value); setOffset(0); }}>
          <option value="">{t("Все отчёты", "All reports")}</option>
          {groups.map(g => <option key={g.name} value={g.name}>{g.name}</option>)}
        </select>
      </label>
      <label>{t("Баг / Не баг", "Bug / No bug")}
        <select value={outcome} onChange={e => { setOutcome(e.target.value); setOffset(0); }}>
          <option value="">{t("Все результаты", "All results")}</option>
          {["bug", "clean", "failed", "empty", "unknown"].map(value => <option key={value} value={value}>{outcomeLabel(value)}</option>)}
        </select>
      </label>
      <label>{t("Сортировка", "Sort by")}
        <select value={sort} onChange={e => changeSort(e.target.value)}>
          <option value="saved">{t("Дата сохранения", "Saved date")}</option>
          <option value="date">{t("Дата коммита", "Commit date")}</option>
          <option value="author">{t("Автор", "Author")}</option>
          <option value="subject">{t("Тема", "Subject")}</option>
          <option value="branch">{t("Ветка", "Branch")}</option>
          <option value="repository">{t("Репозиторий", "Repository")}</option>
          <option value="sha">SHA</option>
          <option value="outcome">{t("Результат", "Result")}</option>
        </select>
      </label>
      <label>{t("Порядок", "Order")}
        <select value={String(descending)} onChange={e => { setDescending(e.target.value === "true"); setOffset(0); }}>
          <option value="true">{t("По убыванию", "Descending")}</option>
          <option value="false">{t("По возрастанию", "Ascending")}</option>
        </select>
      </label>
      <button onClick={() => setRefresh(v => v + 1)} disabled={loading}>{t("Обновить", "Refresh")}</button>
    </div>
    {report && <div className="report-downloads">
      <span>{t("Полный отчёт по ветке:", "Full branch report:")}</span>
      <a href={branchUrl + "?format=html"} target="_blank" rel="noopener noreferrer">{t("Открыть", "Open")}</a>
      <a href={branchUrl + "?download=true"}>Markdown</a>
      <a href={branchUrl + "?format=html&download=true"}>HTML</a>
    </div>}
    {error && <p className="error" role="alert">{error} <button onClick={() => setRefresh(v => v + 1)}>{t("Повторить", "Retry")}</button></p>}
    <p role="status" className="report-status">{loading ? t("Загрузка отчётов…", "Loading reports…") : error ? "" : t("Найдено проверок: ", "Reviews found: ") + page.total}</p>
    {!loading && !error && page.total === 0 && <p className="empty">{search || outcome ? t("Ничего не найдено. Измените запрос или фильтры.", "No matches. Try another query or filters.") : report ? t("В этом отчёте нет распознанных коммитов. Откройте полный отчёт выше.", "No recognized commits in this report. Open the full report above.") : t("После первой проверки здесь появятся отчёты. Старые отчёты без коммитов доступны в списке веток.", "Reviews appear here after the first check. Older reports without commit entries are available in the branch list.")}</p>}
    {!error && <div className="review-list" aria-busy={loading}>
      {page.items.map(entry => {
        const url = "/api/v1/report-entries/" + entry.id;
        return <article className="review-entry" key={entry.id}>
          <div className={"review-outcome review-outcome-" + entry.outcome}>
            <span className="review-outcome-label">{t("Баг / Не баг", "Bug / No bug")}</span>
            <strong>{outcomeLabel(entry.outcome)}</strong>
          </div>
          <div className="review-content">
          <div className="review-entry-heading">
            <h2><a href={url + "?format=html"} target="_blank" rel="noopener noreferrer"><code>{entry.shortSha}</code> {entry.subject || t("Без темы", "No subject")}</a></h2>
            <time dateTime={entry.savedAt} title={entry.legacy ? t("Дата изменения исходного файла", "Original file modified date") : t("Дата сохранения проверки", "Review saved date")}>{new Date(entry.savedAt).toLocaleString()}</time>
          </div>
          <p className="review-location" title={entry.repository}>{entry.repository}</p>
          <p className="review-meta">{entry.branch || t("Ветка неизвестна", "Unknown branch")} · {entry.author || t("Автор неизвестен", "Unknown author")}{entry.commitDate && <> · {t("Коммит: ", "Commit: ")}{new Date(entry.commitDate).toLocaleString()}</>}{entry.legacy && <> · {t("Импортирован", "Imported")}</>}</p>
          <div className="report-downloads">
            <a href={url + "?format=html"} target="_blank" rel="noopener noreferrer">{t("Открыть проверку", "Open review")}</a>
            <a href={url + "?download=true"}>{t("Скачать Markdown", "Download Markdown")}</a>
            <a href={url + "?format=html&download=true"}>{t("Скачать HTML", "Download HTML")}</a>
            <button onClick={() => { setReport(entry.reportName); setOffset(0); }}>{t("Все коммиты ветки", "All branch commits")}</button>
          </div>
          </div>
        </article>;
      })}
    </div>}
    {!error && page.total > 0 && <nav className="report-pagination" aria-label={t("Страницы отчётов", "Report pages")}>
      <button disabled={loading || offset === 0} onClick={() => setOffset(Math.max(0, offset - pageSize))}>{t("Назад", "Previous")}</button>
      <span>{page.offset + 1}–{page.offset + page.items.length} / {page.total}</span>
      <button disabled={loading || offset + pageSize >= page.total} onClick={() => setOffset(offset + pageSize)}>{t("Далее", "Next")}</button>
    </nav>}
  </>;
}
