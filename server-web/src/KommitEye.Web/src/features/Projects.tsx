import { useEffect, useState } from "react";
import { api } from "../api";
import { RepositoryConnection } from "./RepositoryConnection";
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
  const [data, setData] = useState<Data>({
      projects: [],
      statuses: [],
      revision: 0,
    }),
    [error, setError] = useState(""),
    [edit, setEdit] = useState<Data | null>(null),
    [selected, setSelected] = useState(""),
    [log, setLog] = useState(""),
    [details, setDetails] = useState(false),
    [logProject, setLogProject] = useState<Data | null>(null),
    [sha, setSha] = useState(""),
    [commits, setCommits] = useState<Data[]>([]),
    [offset, setOffset] = useState(0),
    [head, setHead] = useState(""),
    [hasMore, setHasMore] = useState(true),
    [branches, setBranches] = useState<string[]>([]),
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
  useEffect(() => {
    setBranches([]);
  }, [edit?.repositoryPath]);
  const fields = [
    "displayName",
    "branchRef",
    "pollIntervalSeconds",
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
      branchRef: "main",
      pollIntervalSeconds: 60,
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
          <p>
            {t(
              "Пути и Git-ключи относятся к компьютеру сервера.",
              "Paths and Git keys refer to the server computer.",
            )}
          </p>
          <RepositoryConnection value={edit} set={setEdit} />
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
              onClick={async () =>
                setBranches(
                  await api("/repositories/branches", {
                    path: edit.repositoryPath,
                  }),
                )
              }
            >
              {t("Загрузить ветки", "Load branches")}
            </Action>
            {branches.length > 0 && (
              <select
                aria-label="Repository branches"
                value={edit.branchRef}
                onChange={(e) =>
                  setEdit({ ...edit, branchRef: e.target.value })
                }
              >
                {branches.map((b) => (
                  <option key={b}>{b}</option>
                ))}
              </select>
            )}
          </div>
          <div className="buttons">
            <Action
              onClick={async () => {
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
                      </td>
                      <td className="mono">
                        {status.commit?.slice(0, 12) || "—"}
                      </td>
                      <td>
                        <div className="buttons">
                          <Action
                            onClick={async () => {
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
                <select
                  className="commit-list"
                  size={6}
                  aria-label="Commits"
                  onChange={(e) => setSha(e.target.value)}
                >
                  {commits.map((c) => (
                    <option key={c.sha} value={c.sha}>
                      {c.sha?.slice(0, 10)} {c.subject}
                    </option>
                  ))}
                </select>
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
