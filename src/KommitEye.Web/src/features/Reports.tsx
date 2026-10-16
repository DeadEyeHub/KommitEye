import { useEffect, useState } from "react";
import { api } from "../api";
import {
  t,
  language,
  requestId,
  Field,
  ObjectFields,
  Action,
  Heading,
  Modal,
  type Data,
} from "../ui";
export function Reports({ tick }: { tick: number }) {
  const [list, setList] = useState<Data[]>([]),
    [error, setError] = useState("");
  useEffect(() => {
    api<Data[]>("/reports")
      .then(setList)
      .catch((e) => setError(e.message));
  }, [tick]);
  return (
    <>
      <Heading
        title={t("Отчёты", "Reports")}
        description={t(
          "Сохранённые результаты проверок по репозиториям и веткам.",
          "Saved reviews by repository and branch.",
        )}
      />
      {error && <p className="error">{error}</p>}
      {!list.length && (
        <p className="empty">
          {t(
            "После первой проверки здесь появится отчёт.",
            "Your first completed review will appear here.",
          )}
        </p>
      )}
      {list.map((r) => (
        <div className="report-row" key={r.name}>
          <a
            href={
              "/api/v1/reports/" + encodeURIComponent(r.name) + "?format=html"
            }
            target="_blank"
            rel="noopener noreferrer"
          >
            {r.name}
          </a>
          <small>{new Date(r.modified).toLocaleString()}</small>
          <a href={"/api/v1/reports/" + encodeURIComponent(r.name)} download>
            {t("Скачать Markdown", "Download Markdown")}
          </a>
          <a
            href={
              "/api/v1/reports/" +
              encodeURIComponent(r.name) +
              "?format=html&download=true"
            }
            download
          >
            {t("Скачать HTML", "Download HTML")}
          </a>
        </div>
      ))}
    </>
  );
}
