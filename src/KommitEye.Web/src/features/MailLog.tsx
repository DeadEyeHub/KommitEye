import { useEffect, useRef, useState } from "react";
import { api } from "../api";
import { t } from "../ui";

export function MailLog() {
  const [text, setText] = useState(""),
    [error, setError] = useState(""),
    [follow, setFollow] = useState(true);
  const pre = useRef<HTMLPreElement>(null);
  useEffect(() => {
    let active = true;
    const load = async () => {
      try {
        const value = await api<string>("/mail/log");
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
  }, []);
  useEffect(() => {
    if (follow && pre.current) pre.current.scrollTop = pre.current.scrollHeight;
  }, [text, follow]);
  return (
    <section className="panel" aria-labelledby="mail-log-title">
      <div className="section-heading">
        <h2 id="mail-log-title">{t("Журнал почты", "Mail log")}</h2>
        <label className="check">
          <input
            type="checkbox"
            checked={follow}
            onChange={(e) => setFollow(e.target.checked)}
          />
          {t("Автопрокрутка", "Auto-scroll")}
        </label>
      </div>
      <p className="muted">
        {t(
          "Очередь, попытки отправки, результат и ошибки для всех почтовых сервисов. Обновление каждые 2 секунды.",
          "Queue, send attempts, results and errors for all mail providers. Updates every 2 seconds.",
        )}
      </p>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <pre className="log" ref={pre} tabIndex={0}>
        {text || t("Почтовых событий пока нет.", "No mail events yet.")}
      </pre>
    </section>
  );
}
