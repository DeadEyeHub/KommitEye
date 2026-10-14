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
export function Jobs({ tick }: { tick: number }) {
  const [jobs, setJobs] = useState<Data[]>([]),
    [error, setError] = useState("");
  useEffect(() => {
    api<Data[]>("/jobs")
      .then(setJobs)
      .catch((e) => setError(e.message));
  }, [tick]);
  return (
    <>
      <Heading
        title={t("Задания", "Jobs")}
        description={t(
          "История запусков сохраняется после перезапуска сервера.",
          "Run history survives server restarts.",
        )}
      />
      {error && <p className="error">{error}</p>}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>{t("Запуск", "Created")}</th>
              <th>{t("Операция", "Operation")}</th>
              <th>{t("Состояние", "Status")}</th>
              <th>{t("Подробности", "Details")}</th>
            </tr>
          </thead>
          <tbody>
            {jobs.map((j) => (
              <tr key={j.id}>
                <td>{new Date(j.createdAt).toLocaleString()}</td>
                <td>
                  {j.kind}
                  <small>{j.projectId}</small>
                </td>
                <td>
                  <span className="badge">{j.status}</span>
                </td>
                <td>{j.error || j.revision || "—"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!jobs.length && (
        <p className="empty">
          {t(
            "Проверки ещё не запускались.",
            "No reviews have been started yet.",
          )}
        </p>
      )}
    </>
  );
}
