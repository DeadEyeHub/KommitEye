import { useEffect, useState } from "react";
import { api } from "../api";
import { Action, Field, Modal, t, type Data } from "../ui";

function ServerBrowser({
  initial,
  files,
  choose,
  close,
}: {
  initial: string;
  files: boolean;
  choose: (path: string) => void;
  close: () => void;
}) {
  const [path, setPath] = useState(initial),
    [listing, setListing] = useState<Data | null>(null),
    [error, setError] = useState("");
  const load = async (value: string) => {
    const result = await api<Data>("/repositories/browse", {
      path: value,
      files,
    });
    setListing(result);
    setPath(result.path);
    setError("");
  };
  useEffect(() => {
    void load(initial).catch((e) => setError(e.message));
  }, []);
  return (
    <Modal titleId="browser-title" onClose={close}>
      <h2 id="browser-title">{t("Выбор на сервере", "Browse server")}</h2>
      <p>
        {t(
          "Это файлы компьютера, на котором работает сервер KommitEye.",
          "These files belong to the computer running the KommitEye server.",
        )}
      </p>
      <Field name={t("Путь", "Path")} value={path} onChange={setPath} />
      <div className="buttons">
        <Action onClick={() => load(path)}>
          {t("Открыть путь", "Open path")}
        </Action>
        <Action onClick={() => load("")}>{t("Диски", "Drives")}</Action>
        {listing?.path && (
          <Action onClick={() => load(listing.parent || "")}>
            {t("На уровень выше", "Parent folder")}
          </Action>
        )}
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <ul className="server-files">
        {listing?.entries.map((entry: Data) => (
          <li key={entry.path}>
            <Action
              onClick={async () => {
                if (entry.directory) await load(entry.path);
                else choose(entry.path);
              }}
            >
              {entry.name}
              {entry.directory ? "/" : ""}
            </Action>
          </li>
        ))}
      </ul>
      {listing && !listing.entries.length && (
        <p>{t("Папка пуста.", "Folder is empty.")}</p>
      )}
      {listing?.truncated && (
        <p>
          {t(
            "Показаны первые 1000 элементов. Укажите точный путь для остальных.",
            "Showing the first 1000 entries. Enter an exact path for other entries.",
          )}
        </p>
      )}
      <div className="buttons">
        {!files && listing?.path && (
          <button className="primary" onClick={() => choose(listing.path)}>
            {t("Выбрать эту папку", "Select this folder")}
          </button>
        )}
        <button onClick={close}>{t("Отмена", "Cancel")}</button>
      </div>
    </Modal>
  );
}

export function RepositoryConnection({
  value,
  set,
}: {
  value: Data;
  set: (value: Data) => void;
}) {
  const [browse, setBrowse] = useState(""),
    [result, setResult] = useState("");
  useEffect(
    () => setResult(""),
    [
      value.repositoryPath,
      value.branchRef,
      value.gitAuthenticationMode,
      value.sshPrivateKeyPath,
      value.plinkPath,
    ],
  );
  const mode = value.gitAuthenticationMode;
  const pathField = (key: string) => (
    <div className="path-field">
      <Field
        name={key}
        value={value[key]}
        onChange={(v) => set({ ...value, [key]: v })}
      />
      <button onClick={() => setBrowse(key)}>
        {t("Обзор на сервере…", "Browse server…")}
      </button>
    </div>
  );
  return (
    <section className="repository-connection">
      <h3>{t("Подключение репозитория", "Repository connection")}</h3>
      {pathField("repositoryPath")}
      <Field name="gitAuthenticationMode" value={mode} onChange={() => {}}>
        <select
          value={mode}
          onChange={(e) =>
            set({ ...value, gitAuthenticationMode: e.target.value })
          }
        >
          <option value="auto">
            {t("Автоопределение по remote", "Detect from remote automatically")}
          </option>
          <option value="ssh-agent">SSH Agent</option>
          <option value="ssh-key">
            {t("SSH-ключ из файла", "SSH key file")}
          </option>
          <option value="putty-key">
            {t("Ключ PuTTY (.ppk)", "PuTTY key (.ppk)")}
          </option>
          <option value="https">HTTPS</option>
        </select>
      </Field>
      {(mode === "ssh-key" || mode === "putty-key") &&
        pathField("sshPrivateKeyPath")}
      {mode === "putty-key" && pathField("plinkPath")}
      <p className="muted">
        {mode === "https"
          ? t(
              "Используются учётные данные Git Credential Manager пользователя сервера. Remote должен иметь адрес HTTPS.",
              "Uses the server user's Git Credential Manager credentials. The remote must use HTTPS.",
            )
          : mode === "putty-key"
            ? t(
                "Выберите .ppk. Пустой путь Plink включает автоматический поиск. Зашифрованный ключ загрузите в Pageant пользователя сервера.",
                "Select a .ppk file. Leave Plink empty for automatic detection. Load encrypted keys into the server user's Pageant.",
              )
            : mode === "ssh-key"
              ? t(
                  "Путь к приватному ключу на сервере. Для ключа с паролем используйте SSH Agent.",
                  "Private key path on the server. Use SSH Agent for passphrase-protected keys.",
                )
              : t(
                  "Используются Git и SSH-ключи пользователя, запустившего сервер. При запуске как службы это учётная запись службы.",
                  "Uses Git and SSH keys of the account running the server, including the service account when running as a service.",
                )}
      </p>
      <div className="buttons">
        <Action
          onClick={async () => {
            const detected = await api<Data>("/repositories/detect", value);
            set({ ...value, gitAuthenticationMode: detected.mode });
          }}
        >
          {t("Определить по remote", "Detect from remote")}
        </Action>
        <Action
          onClick={async () => {
            const response = await api<Data>("/repositories/test", value);
            setResult(
              response.remote
                ? t("Доступ подтверждён: ", "Access confirmed: ") +
                    response.remote
                : t(
                    "Локальный репозиторий проверен; remote не настроен.",
                    "Local repository verified; no remote configured.",
                  ),
            );
          }}
        >
          {t("Проверить подключение", "Test connection")}
        </Action>
      </div>
      {result && <p role="status">{result}</p>}
      {browse && (
        <ServerBrowser
          initial={value[browse] || value.repositoryPath || ""}
          files={browse !== "repositoryPath"}
          close={() => setBrowse("")}
          choose={(path) => {
            set({ ...value, [browse]: path });
            setBrowse("");
          }}
        />
      )}
    </section>
  );
}
