import { useEffect, useState, useRef, type ReactNode } from "react";
export type Data = Record<string, any>;
export function Modal({
  children,
  onClose,
}: {
  children: ReactNode;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    ref.current?.showModal();
    return () => ref.current?.close();
  }, []);
  return (
    <dialog
      ref={ref}
      className="modal"
      aria-labelledby="smtp-title"
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
    >
      {children}
    </dialog>
  );
}
export let language = localStorage.getItem("language") || "ru";
export const t = (ru: string, en: string) => (language === "ru" ? ru : en);
// randomUUID is unavailable on plain HTTP LAN origins; getRandomValues is supported there.
export const requestId = () => {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 15) | 64;
  bytes[8] = (bytes[8] & 63) | 128;
  const s = Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
  return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
};
const labels: Record<string, [string, string]> = {
  temperature: ["Температура", "Temperature"],
  topP: ["Top P", "Top P"],
  maxRounds: ["Раундов запросов модели", "Model request rounds"],
  reminderRequests: [
    "Напоминать при остатке запросов",
    "Remind when requests remain",
  ],
  maxToolCalls: ["Вызовов инструментов", "Tool call limit"],
  toolOutputLimit: [
    "Лимит результатов инструментов, символов",
    "Tool output limit, characters",
  ],
  reasoningLimit: [
    "Лимит рассуждений, символов",
    "Reasoning limit, characters",
  ],
  contentLimit: ["Лимит ответа, символов", "Content limit, characters"],
  toolArgumentLimit: [
    "Лимит аргументов инструмента, символов",
    "Tool arguments limit, characters",
  ],
  transportLimit: [
    "Лимит HTTP-ответа, символов",
    "HTTP response limit, characters",
  ],
  snapshotMb: ["Размер Git-снимков, MiB", "Git snapshot size, MiB"],
  displayName: ["Название", "Name"],
  repositoryPath: [
    "Папка репозитория на сервере",
    "Repository folder on server",
  ],
  branchRef: ["Ветка", "Branch"],
  pollIntervalSeconds: [
    "Интервал проверки, секунд",
    "Polling interval, seconds",
  ],
  pullEnabled: ["Получать изменения из remote", "Fetch remote changes"],
  fetchSubmodules: ["Получать сабмодули", "Fetch submodules"],
  updateSubmodulesAfterAdvance: [
    "Обновлять рабочие копии сабмодулей",
    "Update submodule working copies",
  ],
  quietHoursEnabled: ["Пауза по расписанию", "Scheduled pause"],
  quietHoursStart: ["Начало паузы", "Pause starts"],
  quietHoursEnd: ["Окончание паузы", "Pause ends"],
  gitAuthenticationMode: ["Авторизация Git", "Git authentication"],
  sshPrivateKeyPath: ["Путь к SSH-ключу на сервере", "SSH key path on server"],
  plinkPath: ["Путь к Plink", "Plink path"],
  name: ["Название профиля", "Profile name"],
  endpoint: ["Адрес API модели", "Model API endpoint"],
  model: ["Модель", "Model"],
  apiKey: ["Новый API-ключ", "New API key"],
  apiKeyEnvironment: [
    "Переменная окружения с ключом",
    "API key environment variable",
  ],
  reviewMinutes: ["Лимит проверки, минут", "Review timeout, minutes"],
  responseInactivityMinutes: [
    "Ожидание ответа, минут",
    "Response inactivity, minutes",
  ],
  availabilityCheckMinutes: [
    "Интервал проверки модели, минут",
    "Availability check, minutes",
  ],
  maxRetries: ["Повторные попытки", "Retries"],
  maxTokens: [
    "Лимит токенов (0 — настройка модели)",
    "Max tokens (0 — model default)",
  ],
  enabled: ["Включить уведомления", "Enable notifications"],
  sendToAuthor: [
    "Отправлять также автору коммита",
    "Also send to commit author",
  ],
  sendWithoutBugs: [
    "Отправлять при отсутствии замечаний",
    "Send clean reviews",
  ],
  host: ["SMTP-сервер", "SMTP host"],
  port: ["Порт", "Port"],
  security: ["Защита соединения", "Connection security"],
  username: ["Имя пользователя", "Username"],
  from: ["E-mail отправителя", "Sender email"],
  recipients: ["Получатели через ;", "Recipients separated by ;"],
  attachmentFormat: ["Формат отчёта", "Report format"],
};
export function Field({
  name,
  value,
  onChange,
  type,
  children,
}: {
  name: string;
  value: any;
  onChange: (v: any) => void;
  type?: string;
  children?: ReactNode;
}) {
  const label = labels[name] ? t(...labels[name]) : name;
  if (typeof value === "boolean")
    return (
      <label className="check">
        <input
          type="checkbox"
          checked={value}
          onChange={(e) => onChange(e.target.checked)}
        />
        {label}
      </label>
    );
  return (
    <label className="field">
      <span>{label}</span>
      {children || (
        <input
          type={type || (typeof value === "number" ? "number" : "text")}
          value={value ?? ""}
          onChange={(e) =>
            onChange(
              typeof value === "number"
                ? Number(e.target.value)
                : e.target.value,
            )
          }
        />
      )}
    </label>
  );
}
export function ObjectFields({
  value,
  set,
  keys,
}: {
  value: Data;
  set: (v: Data) => void;
  keys: string[];
}) {
  return (
    <>
      {keys.map((key) => (
        <Field
          key={key}
          name={key}
          value={value[key]}
          onChange={(v) => set({ ...value, [key]: v })}
          type={key === "apiKey" ? "password" : undefined}
        />
      ))}
    </>
  );
}
export function Action({
  children,
  onClick,
  danger = false,
}: {
  children: ReactNode;
  onClick: () => Promise<unknown>;
  danger?: boolean;
}) {
  const [busy, setBusy] = useState(false),
    [error, setError] = useState("");
  return (
    <span className="action">
      <button
        className={danger ? "danger" : ""}
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setError("");
          try {
            await onClick();
          } catch (e) {
            setError(String((e as Error).message));
          } finally {
            setBusy(false);
          }
        }}
      >
        {busy ? t("Подождите…", "Working…") : children}
      </button>
      {error && (
        <span role="alert" className="error">
          {error}
        </span>
      )}
    </span>
  );
}
export function Heading({
  title,
  description,
  children,
}: {
  title: string;
  description: string;
  children?: ReactNode;
}) {
  return (
    <header className="page-heading">
      <div>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {children}
    </header>
  );
}

export function setLanguage(value: string) {
  language = value;
  localStorage.setItem("language", value);
}
