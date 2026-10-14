import { useEffect, useState } from "react";
import { api } from "../api";
import { MailLog } from "./MailLog";
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
export function Mail() {
  const [provider, setProvider] = useState("smtp"),
    [data, setData] = useState<Data | null>(null),
    [password, setPassword] = useState(""),
    [clear, setClear] = useState(false),
    [message, setMessage] = useState(""),
    [smtp, setSmtp] = useState<Data | null>(null);
  const load = () =>
    api("/mail/" + provider)
      .then(setData)
      .catch((e) => setMessage(e.message));
  useEffect(() => {
    setData(null);
    setMessage("");
    setPassword("");
    void load();
  }, [provider]);
  const save = async (test = false) => {
    await api("/mail/" + provider + (test ? "/test" : ""), {
      revision: data!.revision,
      settings: data!.settings,
      password,
      clearPassword: clear,
      activate: data!.active,
    });
    if (!test) {
      await load();
      setPassword("");
      setClear(false);
    }
    setMessage(
      test
        ? t(
            "Письмо принято почтовым сервисом. Доставка в ящик не подтверждена.",
            "Provider accepted the message. Inbox delivery is not confirmed.",
          )
        : t("Сохранено", "Saved"),
    );
  };
  return (
    <>
      <Heading
        title={t("Почта", "Mail")}
        description={t(
          "При сбое проверки письмо отправляется получателям, без добавления автора.",
          "Failed reviews go to configured recipients without adding the commit author.",
        )}
      />
      <div className="tabs">
        {["smtp", "gmail", "outlook"].map((p) => (
          <button
            key={p}
            className={provider === p ? "active" : ""}
            onClick={() => setProvider(p)}
          >
            {p === "smtp" ? "SMTP" : p === "gmail" ? "Google" : "Outlook"}
          </button>
        ))}
      </div>
      {data && (
        <section className="panel">
          <div className="section-heading">
            <h2>
              {provider === "smtp"
                ? "SMTP"
                : data.account?.address ||
                  t("Аккаунт не подключён", "Account not connected")}
            </h2>
            {provider === "smtp" && (
              <button
                onClick={() =>
                  setSmtp({
                    ...data.settings,
                    _password: password,
                    _clear: clear,
                  })
                }
              >
                {t("Настройки SMTP-сервера", "SMTP server settings")}
              </button>
            )}
          </div>
          {provider !== "smtp" && (
            <OAuth provider={provider} onComplete={load} />
          )}
          {provider !== "smtp" && data.account && (
            <Action
              onClick={async () => {
                await api("/mail/" + provider + "/disconnect", {});
                await load();
              }}
            >
              {t("Отключить аккаунт", "Disconnect account")}
            </Action>
          )}
          <div className="form-grid">
            <ObjectFields
              value={data.settings}
              set={(settings) => setData({ ...data, settings })}
              keys={[
                "enabled",
                "sendToAuthor",
                "sendWithoutBugs",
                "recipients",
              ]}
            />
            <Field
              name="attachmentFormat"
              value={data.settings.attachmentFormat}
              onChange={() => {}}
            >
              <select
                value={data.settings.attachmentFormat}
                onChange={(e) =>
                  setData({
                    ...data,
                    settings: {
                      ...data.settings,
                      attachmentFormat: e.target.value,
                    },
                  })
                }
              >
                <option>html</option>
                <option>md</option>
              </select>
            </Field>
          </div>
          <label className="check">
            <input
              type="checkbox"
              checked={data.active}
              onChange={(e) => setData({ ...data, active: e.target.checked })}
            />
            {t("Использовать для отправки", "Use as active sender")}
          </label>
          <div className="buttons">
            <Action onClick={() => save()}>{t("Сохранить", "Save")}</Action>
            <Action onClick={() => save(true)}>
              {t("Тестовое письмо", "Send test email")}
            </Action>
            <Action
              onClick={async () => {
                await api("/mail/retry", {});
                await load();
              }}
            >
              {t("Повторить неотправленные", "Retry failed mail")}
            </Action>
          </div>
          <p>
            {t("В очереди", "Pending")}: {data.pending} ·{" "}
            {t("Ошибок отправки", "Failed")}: {data.failed}
          </p>
          <p role="status">{message}</p>
        </section>
      )}
      <MailLog />
      {smtp && (
        <Modal onClose={() => setSmtp(null)}>
          <h2 id="smtp-title">{t("SMTP-сервер", "SMTP server")}</h2>
          <div className="form-grid">
            <ObjectFields
              value={smtp}
              set={setSmtp}
              keys={["host", "port", "username", "from"]}
            />
            <Field name="security" value={smtp.security} onChange={() => {}}>
              <select
                value={smtp.security}
                onChange={(e) => setSmtp({ ...smtp, security: e.target.value })}
              >
                <option>StartTls</option>
                <option>SslOnConnect</option>
                <option>None</option>
              </select>
            </Field>
            <label className="field">
              <span>
                {t(
                  "Пароль (пусто — сохранить текущий)",
                  "Password (blank keeps saved password)",
                )}
              </span>
              <input
                type="password"
                autoComplete="new-password"
                value={smtp._password}
                onChange={(e) =>
                  setSmtp({ ...smtp, _password: e.target.value })
                }
              />
            </label>
          </div>
          <label className="check">
            <input
              type="checkbox"
              checked={smtp._clear}
              onChange={(e) => setSmtp({ ...smtp, _clear: e.target.checked })}
            />
            {t("Удалить сохранённый пароль", "Clear saved password")}
          </label>
          <p>
            {t(
              "После закрытия окна нажмите «Сохранить» в разделе почты.",
              "Click Save in Mail after closing this dialog.",
            )}
          </p>
          <div className="buttons">
            <button
              className="primary"
              onClick={() => {
                const { _password, _clear, ...settings } = smtp;
                setData({ ...data, settings });
                setPassword(_password);
                setClear(_clear);
                setSmtp(null);
              }}
            >
              OK
            </button>
            <button onClick={() => setSmtp(null)}>
              {t("Отмена", "Cancel")}
            </button>
          </div>
        </Modal>
      )}
    </>
  );
}
function OAuth({
  provider,
  onComplete,
}: {
  provider: string;
  onComplete: () => Promise<unknown>;
}) {
  const [configuration, setConfiguration] = useState(""),
    [state, setState] = useState<Data | null>(null);
  useEffect(() => {
    if (!state?.id || state.status === "completed" || state.status === "failed")
      return;
    const timer = setInterval(
      () =>
        api("/oauth/" + state.id)
          .then((next) => {
            setState(next);
            if (next.status === "completed") void onComplete();
          })
          .catch(() => {}),
      2000,
    );
    return () => clearInterval(timer);
  }, [state?.id, state?.status]);
  return (
    <div className="oauth">
      <label className="field">
        <span>
          {provider === "gmail"
            ? t(
                "Путь к Google OAuth JSON на сервере",
                "Google OAuth JSON path on server",
              )
            : t(
                "Microsoft client ID; tenant ID (через ;)",
                "Microsoft client ID; tenant ID (separated by ;)",
              )}
        </span>
        <input
          value={configuration}
          onChange={(e) => setConfiguration(e.target.value)}
        />
      </label>
      <Action
        onClick={async () =>
          setState(
            await api("/oauth/" + provider + "/connect", { configuration }),
          )
        }
      >
        {t("Подключить", "Connect")}
      </Action>
      {state && (
        <p role="status">
          {state.message}{" "}
          {state.url && (
            <a href={state.url} target="_blank" rel="noreferrer">
              {t("Открыть вход", "Open sign-in")}
            </a>
          )}{" "}
          {state.code && <strong>{state.code}</strong>}
        </p>
      )}
      <p className="muted">
        {provider === "gmail"
          ? t(
              "Desktop OAuth: вход на компьютере сервера. Для службы настройте Web OAuth и HTTPS callback на сервере.",
              "Desktop OAuth signs in on the server computer. For services, configure Web OAuth and an HTTPS callback on the server.",
            )
          : t(
              "Разрешите public client flows в настройках приложения Microsoft.",
              "Enable public client flows in the Microsoft application settings.",
            )}
      </p>
    </div>
  );
}
