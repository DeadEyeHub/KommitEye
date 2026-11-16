import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { Heading, t } from "../ui";

type Settings = {
  enabled: boolean;
  publicUrl: string;
  codeMinutes: number;
  resendSeconds: number;
  sendsPerHour: number;
  maxAttempts: number;
  lockoutMinutes: number;
  sessionHours: number;
};
const limits = [
  ["codeMinutes", "Срок действия кода, мин", "Code lifetime, minutes", 1, 60],
  [
    "resendSeconds",
    "Интервал отправки кода, с",
    "Resend interval, seconds",
    30,
    3600,
  ],
  [
    "sendsPerHour",
    "Отправок на email за час",
    "Emails per address per hour",
    1,
    30,
  ],
  ["maxAttempts", "Попыток ввода кода", "Code entry attempts", 1, 10],
  [
    "lockoutMinutes",
    "Блокировка после исчерпания попыток, мин",
    "Lockout after failed attempts, minutes",
    1,
    1440,
  ],
  ["sessionHours", "Длительность сессии, ч", "Session lifetime, hours", 1, 720],
] as const;
export function AuthorSettings() {
  const [data, setData] = useState<{
      settings: Settings;
      revision: number;
    } | null>(null),
    [error, setError] = useState(""),
    [message, setMessage] = useState(""),
    [busy, setBusy] = useState(false);
  const load = () =>
    api<{ settings: Settings; revision: number }>("/author-settings")
      .then(setData)
      .catch((e) => setError(e.message));
  useEffect(() => {
    void load();
  }, []);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError("");
    setMessage("");
    try {
      await api("/author-settings", data);
      await load();
      setMessage(t("Настройки сохранены", "Settings saved"));
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <Heading
        title={t("Кабинет автора", "Author portal")}
        description={t(
          "Доступ к своим коммитам по одноразовому коду. Почтовые уведомления настраиваются в разделе «Почта».",
          "Access to personal commit reviews with a one-time code. Configure email notifications in Mail.",
        )}
      />
      {error && (
        <p role="alert" className="error">
          {error}{" "}
          <button
            onClick={() => {
              setError("");
              void load();
            }}
          >
            {t("Перезагрузить настройки", "Reload settings")}
          </button>
        </p>
      )}
      {!data ? (
        <p role="status">{t("Загрузка…", "Loading…")}</p>
      ) : (
        <form className="panel" onSubmit={save}>
          <h2>{t("Общие", "General")}</h2>
          <label className="check">
            <input
              type="checkbox"
              checked={data.settings.enabled}
              onChange={(e) =>
                setData({
                  ...data,
                  settings: { ...data.settings, enabled: e.target.checked },
                })
              }
            />
            {t("Включить кабинет автора", "Enable author portal")}
          </label>
          <label className="field">
            <span>
              {t(
                "Адрес сервера для ссылок в письмах",
                "Public server address for email links",
              )}
            </span>
            <input
              type="url"
              required={data.settings.enabled}
              placeholder="https://reviews.example.com"
              value={data.settings.publicUrl}
              onChange={(e) =>
                setData({
                  ...data,
                  settings: { ...data.settings, publicUrl: e.target.value },
                })
              }
            />
          </label>
          <p className="muted">
            {t(
              "Укажите доступный авторам HTTPS-адрес без пути. Кабинет откроется по адресу /author.",
              "Enter an HTTPS address reachable by authors, without a path. The portal is available at /author.",
            )}
          </p>
          <h2>{t("Вход по коду", "Email code sign-in")}</h2>
          <div className="form-grid">
            {limits.map(([key, ru, en, min, max]) => (
              <label className="field" key={key}>
                <span>{t(ru, en)}</span>
                <input
                  type="number"
                  required
                  min={min}
                  max={max}
                  step={1}
                  value={data.settings[key]}
                  onChange={(e) =>
                    setData({
                      ...data,
                      settings: {
                        ...data.settings,
                        [key]:
                          e.target.value === "" ? "" : Number(e.target.value),
                      },
                    })
                  }
                />
                <small>
                  {min}–{max}
                </small>
              </label>
            ))}
          </div>
          <p>
            {t(
              "Сохранение настроек отменяет выданные коды и завершает текущие сессии авторов.",
              "Saving these settings invalidates outstanding codes and ends current author sessions.",
            )}
          </p>
          <div className="buttons">
            <button className="primary" disabled={busy}>
              {busy ? t("Сохранение…", "Saving…") : t("Сохранить", "Save")}
            </button>
            <a href="/author" target="_blank" rel="noreferrer">
              {t("Открыть кабинет автора", "Open author portal")}
            </a>
          </div>
          {message && <p role="status">{message}</p>}
        </form>
      )}
    </>
  );
}
