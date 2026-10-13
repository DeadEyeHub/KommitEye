import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import { Action, Modal, t } from "../ui";

export function ProjectLog({
  id,
  name,
  close,
}: {
  id: string;
  name: string;
  close: () => void;
}) {
  const [details, setDetails] = useState(true),
    [text, setText] = useState(""),
    [error, setError] = useState(""),
    [follow, setFollow] = useState(true),
    [query, setQuery] = useState(""),
    [confirm, setConfirm] = useState(false);
  const pre = useRef<HTMLPreElement>(null);
  useEffect(() => {
    let active = true;
    setText("");
    setError("");
    setConfirm(false);
    const load = async () => {
      try {
        const value = await api<string>(
          `/projects/${id}/log?details=${details}`,
        );
        if (active) {
          setText(value);
          setError("");
        }
      } catch (e) {
        if (active) setError((e as Error).message);
      }
    };
    void load();
    const timer = window.setInterval(() => void load(), 2000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [id, details]);
  useEffect(() => {
    if (follow && pre.current) pre.current.scrollTop = pre.current.scrollHeight;
  }, [text, follow]);
  const visible = query
    ? text
        .split("\n")
        .filter((line) =>
          line.toLocaleLowerCase().includes(query.toLocaleLowerCase()),
        )
        .join("\n")
    : text;
  return (
    <Modal titleId="log-title" onClose={close}>
      <div className="project-log">
        <h2 id="log-title">
          {t("Логи", "Logs")} · {name}
        </h2>
        <div className="buttons">
          <label className="field">
            <span>{t("Тип лога", "Log type")}</span>
            <select
              value={String(details)}
              onChange={(e) => setDetails(e.target.value === "true")}
            >
              <option value="true">
                {t("Подробный лог модели и Git", "Detailed model and Git log")}
              </option>
              <option value="false">
                {t("Журнал проверок", "Review journal")}
              </option>
            </select>
          </label>
          <label className="check">
            <input
              type="checkbox"
              checked={follow}
              onChange={(e) => setFollow(e.target.checked)}
            />
            {t("Автопрокрутка", "Auto-scroll")}
          </label>
          <button onClick={close}>{t("Закрыть", "Close")}</button>
        </div>
        <input
          aria-label={t("Поиск в логе", "Search log")}
          placeholder={t("Фильтр строк…", "Filter lines…")}
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <pre ref={pre} className="log" tabIndex={0}>
          {visible ||
            (query
              ? t("Совпадений нет.", "No matches.")
              : t("Записей пока нет.", "No entries yet."))}
        </pre>
        <p className="muted">
          {t(
            "Обновление каждые 2 секунды. Показан последний фрагмент лога (до 500 записей / 128 000 символов).",
            "Updates every 2 seconds. Shows the latest log tail (up to 500 entries / 128,000 characters).",
          )}
        </p>
        <div className="buttons">
          <button
            onClick={() => {
              const url = URL.createObjectURL(
                new Blob([text], { type: "text/plain;charset=utf-8" }),
              );
              const link = document.createElement("a");
              link.href = url;
              link.download = `${id}-${details ? "model" : "journal"}.log`;
              link.click();
              setTimeout(() => URL.revokeObjectURL(url), 1000);
            }}
          >
            {t("Скачать показанный фрагмент", "Download displayed tail")}
          </button>
          <button className="danger" onClick={() => setConfirm(true)}>
            {t("Очистить лог…", "Clear log…")}
          </button>
        </div>
        {confirm && (
          <div role="alert">
            <p>
              {t(
                "Удалить выбранный лог и его архив? Отчёты, другой тип лога и счётчики токенов сохранятся.",
                "Delete the selected log and its archive? Reports, the other log type and token counters will be kept.",
              )}
            </p>
            <div className="buttons">
              <Action
                danger
                onClick={async () => {
                  await api(`/projects/${id}/log/clear?details=${details}`, {});
                  setText(
                    await api<string>(`/projects/${id}/log?details=${details}`),
                  );
                  setConfirm(false);
                }}
              >
                {t("Удалить лог и архив", "Delete log and archive")}
              </Action>
              <button onClick={() => setConfirm(false)}>
                {t("Отмена", "Cancel")}
              </button>
            </div>
          </div>
        )}
      </div>
    </Modal>
  );
}
