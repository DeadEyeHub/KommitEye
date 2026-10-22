import { useEffect, useState } from "react";
import { api } from "../api";
import { RepositoryConnection } from "./RepositoryConnection";
import { BranchSelect } from "./BranchSelect";
import { ProjectLog } from "./ProjectLog";
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
export function Projects({ tick }: { tick: number }) {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const timer = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(timer);
  }, []);
  const [data, setData] = useState<Data>({
      projects: [],
      statuses: [],
      revision: 0,
    }),
    [error, setError] = useState(""),
    [edit, setEdit] = useState<Data | null>(null),
    [deleting, setDeleting] = useState<Data | null>(null),
    [selected, setSelected] = useState(""),
    [log, setLog] = useState(""),
    [details, setDetails] = useState(false),
    [logProject, setLogProject] = useState<Data | null>(null),
    [sha, setSha] = useState(""),
    [commits, setCommits] = useState<Data[]>([]),
    [offset, setOffset] = useState(0),
    [head, setHead] = useState(""),
    [hasMore, setHasMore] = useState(true),
    [usage, setUsage] = useState<Data | null>(null);
  const load = () =>
    api("/projects")
      .then(setData)
      .catch((e) => setError(e.message));
  useEffect(() => {
    void load();
    api("/usage")
      .then(setUsage)
      .catch(() => {});
  }, [tick]);
  useEffect(() => {
    let active = true;
    if (selected)
      api<string>(`/projects/${selected}/log?details=${details}`)
        .then((value) => {
          if (active) setLog(value);
        })
        .catch((e) => setError(e.message));
    return () => {
      active = false;
    };
  }, [selected, details, tick]);
  const fields = [
    "pollIntervalSeconds",
    "autoStart",
    "pullEnabled",
    "fetchSubmodules",
    "updateSubmodulesAfterAdvance",
    "quietHoursEnabled",
    "quietHoursStart",
    "quietHoursEnd",
  ];
  const newProject = () =>
    setEdit({
      _revision: data.revision,
      projectId: "",
      displayName: "",
      repositoryPath: "",
      branchRef: "",
      pollIntervalSeconds: 60,
      autoStart: false,
      pullEnabled: true,
      fetchSubmodules: false,
      updateSubmodulesAfterAdvance: true,
      quietHoursEnabled: false,
      quietHoursStart: "09:00",
      quietHoursEnd: "18:00",
      gitAuthenticationMode: "auto",
      sshPrivateKeyPath: "",
      plinkPath: "",
      language,
    });
  return (
    <>
      <Heading
        title={t("Проекты", "Projects")}
        description={t(
          "Проверки продолжаются, даже когда браузер закрыт.",
          "Reviews keep running when you close the browser.",
        )}
      >
        <button className="primary" onClick={newProject}>
          {t("Добавить проект", "Add project")}
        </button>
      </Heading>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      {edit ? (
        <section className="panel">
          <h2>{t("Настройки проекта", "Project settings")}</h2>
          <Field
            name="displayName"
            value={edit.displayName}
            onChange={(displayName) => setEdit({ ...edit, displayName })}
          />
          <p>
            {t(
              "Пути и Git-ключи относятся к компьютеру сервера.",
              "Paths and Git keys refer to the server computer.",
            )}
          </p>
          <RepositoryConnection value={edit} set={setEdit} />
          <BranchSelect
            path={edit.repositoryPath}
            value={edit.branchRef}
            change={(branchRef) =>
              setEdit((current) =>
                current ? { ...current, branchRef } : current,
              )
            }
          />
          <div className="form-grid">
            <ObjectFields value={edit} set={setEdit} keys={fields} />
            <label className="field">
              <span>{t("Язык отчётов", "Report language")}</span>
              <select
                value={edit.language}
                onChange={(e) => setEdit({ ...edit, language: e.target.value })}
              >
                <option value="ru">Русский</option>
                <option value="en">English</option>
              </select>
            </label>
          </div>
          <div className="buttons">
            <Action
              onClick={async () => {
                validateSelection(edit);
                await api("/projects", {
                  revision: edit._revision,
                  settings: edit,
                });
                setEdit(null);
                await load();
              }}
            >
              {t("Сохранить", "Save")}
            </Action>
            <button onClick={() => setEdit(null)}>
              {t("Отмена", "Cancel")}
            </button>
          </div>
        </section>
      ) : (
        <>
          {usage && (
            <p className="muted">
              {t("Токенов сегодня", "Tokens today")}:{" "}
              {usage.today.tokens.toLocaleString()} · {t("Всего", "Total")}:{" "}
              {usage.total.tokens.toLocaleString()} ·{" "}
              {t("Запросов", "Requests")}:{" "}
              {usage.total.requests.toLocaleString()}
            </p>
          )}
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>{t("Проект / репозиторий", "Project / repository")}</th>
                  <th>{t("Состояние", "Status")}</th>
                  <th>{t("Коммит", "Commit")}</th>
                  <th>{t("Действия", "Actions")}</th>
                </tr>
              </thead>
              <tbody>
                {data.projects.map((p: Data) => {
                  const status =
                    data.statuses.find(
                      (s: Data) => s.projectId === p.projectId,
                    ) || {};
                  return (
                    <tr
                      key={p.projectId}
                      className={selected === p.projectId ? "selected" : ""}
                    >
                      <td>
                        <button
                          className="text-button"
                          onClick={() => {
                            setSelected(p.projectId);
                            setCommits([]);
                            setOffset(0);
                            setHead("");
                            setHasMore(true);
                            setSha("");
                          }}
                        >
                          {p.displayName || t("Без названия", "Untitled")}
                        </button>
                        <small>
                          {p.repositoryPath ||
                            t(
                              "Укажите папку репозитория",
                              "Set repository folder",
                            )}{" "}
                          · {p.branchRef || "—"}
                        </small>
                      </td>
                      <td>
                        <span
                          className={
                            "badge " + (status.running ? "running" : "")
                          }
                        >
                          {status.stage === "idle"
                            ? t("Остановлен", "Stopped")
                            : status.status}
                        </span>
                        {status.running && (
                          <small>
                            {status.stage}
                            {status.remaining != null
                              ? ` · ${status.remaining}`
                              : ""}
                          </small>
                        )}
                        {status.running && status.nextRun && (
                          <small
                            className="countdown"
                            title={new Date(status.nextRun).toLocaleString()}
                          >
                            {countdown(status.nextRun, now)}
                          </small>
                        )}
                      </td>
                      <td className="mono">
                        {status.commit?.slice(0, 12) || "—"}
                      </td>
                      <td>
                        <div className="buttons">
                          <Action
                            onClick={async () => {
                              if (!status.running) validateSelection(p);
                              await api(
                                `/projects/${p.projectId}/${status.running ? "stop" : "start"}`,
                                { requestKey: requestId() },
                              );
                              await load();
                            }}
                          >
                            {status.running
                              ? t("Остановить", "Stop")
                              : t("Запустить", "Start")}
                          </Action>
                          <button
                            disabled={status.running}
                            onClick={() =>
                              setEdit({ ...p, _revision: data.revision })
                            }
                          >
                            {t("Настроить", "Configure")}
                          </button>
                          <button
                            className="danger"
                            disabled={status.running}
                            title={
                              status.running
                                ? t(
                                    "Сначала остановите проект",
                                    "Stop the project first",
                                  )
                                : undefined
                            }
                            onClick={() =>
                              setDeleting({ ...p, _revision: data.revision })
                            }
                          >
                            {t("Удалить", "Delete")}
                          </button>
                          <button onClick={() => setLogProject(p)}>
                            {t("Логи", "Logs")}
                          </button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          {!data.projects.length && (
            <p className="empty">
              {t(
                "Добавьте первый репозиторий для проверки.",
                "Add your first repository to start reviewing.",
              )}
            </p>
          )}
          {selected && (
            <section className="panel">
              <h2>{t("Выбранный проект", "Selected project")}</h2>
              <div className="buttons">
                <input
                  aria-label="Commit SHA"
                  placeholder="Commit SHA"
                  value={sha}
                  onChange={(e) => setSha(e.target.value)}
                />
                <Action
                  onClick={async () => {
                    if (!sha.trim())
                      throw new Error(t("Введите SHA", "Enter SHA"));
                    await api(`/projects/${selected}/reviews`, {
                      requestKey: requestId(),
                      sha: sha.trim(),
                    });
                    await load();
                  }}
                >
                  {t("Проверить коммит", "Review commit")}
                </Action>
                <Action
                  onClick={async () => {
                    const page = await api<Data>(
                      `/projects/${selected}/commits?offset=${offset}&head=${head}`,
                    );
                    setCommits([...commits, ...page.commits]);
                    setOffset(offset + page.commits.length);
                    setHead(page.head);
                    setHasMore(page.hasMore);
                  }}
                >
                  {hasMore
                    ? t("Загрузить 100 коммитов", "Load 100 commits")
                    : t("Конец истории", "End of history")}
                </Action>
                <Action
                  onClick={async () => {
                    if (!sha.trim())
                      throw new Error(t("Введите SHA", "Enter SHA"));
                    await api(`/projects/${selected}/start-commit`, {
                      requestKey: requestId(),
                      sha: sha.trim(),
                    });
                    setError(
                      t(
                        "Начальный коммит сохранён. Нажмите «Запустить».",
                        "Starting commit saved. Click Start.",
                      ),
                    );
                  }}
                >
                  {t(
                    "Начать мониторинг с этого SHA",
                    "Set monitoring start SHA",
                  )}
                </Action>
              </div>
              {commits.length > 0 && (
                <div
                  className="commit-list"
                  role="radiogroup"
                  aria-label={t("Коммиты", "Commits")}
                >
                  {commits.map((c) => {
                    const author = c.author || t("Автор неизвестен", "Unknown author");
                    const date = c.date ? new Date(c.date).toLocaleString(language === "ru" ? "ru-RU" : "en-GB") : "";
                    const description = c.message || c.subject;
                    return (
                      <label className="commit-row" key={c.sha} title={`${c.sha}\n${author} · ${date}\n\n${description}`}>
                        <input type="radio" name="selected-commit" value={c.sha} checked={sha === c.sha} onChange={() => setSha(c.sha)} />
                        <code>{c.sha?.slice(0, 10)}</code>
                        <span className="commit-author">{author}</span>
                        <time dateTime={c.date}>{date}</time>
                        <span className="commit-description">{c.subject}</span>
                      </label>
                    );
                  })}
                </div>
              )}
              <div className="section-heading">
                <h2>{t("Журнал", "Journal")}</h2>
                <label className="check">
                  <input
                    type="checkbox"
                    checked={details}
                    onChange={(e) => setDetails(e.target.checked)}
                  />
                  {t("Подробный лог модели", "Detailed model log")}
                </label>
              </div>
              <pre className="log">
                {log || t("Событий пока нет.", "No events yet.")}
              </pre>
            </section>
          )}
        </>
      )}
      {deleting && (
        <Modal titleId="delete-project-title" onClose={() => setDeleting(null)}>
          <h2 id="delete-project-title">
            {t("Удалить проект?", "Delete project?")}
          </h2>
          <p>
            {deleting.displayName ||
              deleting.repositoryPath ||
              t("Без названия", "Untitled")}
          </p>
          <p>
            {t(
              "Проект будет удалён из списка. Репозиторий, отчёты, логи и история проверок сохранятся.",
              "The project will be removed from the list. Repository files, reports, logs and review history will be kept.",
            )}
          </p>
          <div className="buttons">
            <Action
              danger
              onClick={async () => {
                await api(`/projects/${deleting.projectId}/delete`, {
                  revision: deleting._revision,
                });
                if (selected === deleting.projectId) {
                  setSelected("");
                  setLog("");
                  setCommits([]);
                }
                setDeleting(null);
                await load();
              }}
            >
              {t("Удалить проект", "Delete project")}
            </Action>
            <button onClick={() => setDeleting(null)}>
              {t("Отмена", "Cancel")}
            </button>
          </div>
        </Modal>
      )}
      {logProject && (
        <ProjectLog
          id={logProject.projectId}
          name={logProject.displayName || logProject.repositoryPath}
          close={() => setLogProject(null)}
        />
      )}
    </>
  );
}

function validateSelection(project: Data) {
  if (!project.repositoryPath?.trim())
    throw new Error(
      t(
        "Выберите папку репозитория в настройках проекта.",
        "Select a repository folder in project settings.",
      ),
    );
  if (!project.branchRef?.trim())
    throw new Error(
      t(
        "Выберите ветку в настройках проекта перед запуском проверки.",
        "Select a branch in project settings before starting a review.",
      ),
    );
}

function countdown(nextRun: string, now: number) {
  const deadline = Date.parse(nextRun);
  if (!Number.isFinite(deadline)) return "";
  const seconds = Math.max(0, Math.ceil((deadline - now) / 1000));
  if (seconds === 0)
    return t("Ожидание следующей проверки…", "Waiting for the next check…");
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const time = [hours, minutes, seconds % 60]
    .map((part) => String(part).padStart(2, "0"))
    .join(":");
  return t("До следующей проверки: ", "Next check in: ") + time;
}
