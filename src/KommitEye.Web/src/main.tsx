import { useEffect, useState, type FormEvent } from "react";
import { createRoot } from "react-dom/client";
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { api } from "./api";
import "./style.css";

import { t, language, setLanguage, Action, type Data } from "./ui";
import { Projects } from "./features/Projects";
import { Models } from "./features/Models";
import { Mail } from "./features/Mail";
import { Prompt } from "./features/Prompt";
import { Reports } from "./features/Reports";
import { Jobs } from "./features/Jobs";
function App() {
  const [signed, setSigned] = useState<boolean | null>(null),
    [password, setPassword] = useState(""),
    [confirmation, setConfirmation] = useState(""),
    [setup, setSetup] = useState<Data | null>(null),
    [error, setError] = useState(""),
    [busy, setBusy] = useState(false);
  const [tab, setTab] = useState("projects"),
    [connection, setConnection] = useState("connecting"),
    [info, setInfo] = useState<Data>({}),
    [tick, setTick] = useState(0),
    [lang, setLang] = useState(language);
  useEffect(() => {
    api<Data>("/setup")
      .then(setSetup)
      .catch((e) => setError(e.message));
    api("/session")
      .then(() => setSigned(true))
      .catch(() => setSigned(false));
    const expired = () => setSigned(false);
    window.addEventListener("session-expired", expired);
    return () => window.removeEventListener("session-expired", expired);
  }, []);
  useEffect(() => {
    if (!signed) return;
    api("/system/info")
      .then(setInfo)
      .catch(() => {});
    const hub = new HubConnectionBuilder()
      .withUrl("/hubs/events")
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Error)
      .build();
    hub.on("changed", () => setTick((n) => n + 1));
    hub.onreconnecting(() => setConnection("reconnecting"));
    hub.onreconnected(() => {
      setConnection("connected");
      setTick((n) => n + 1);
    });
    hub.onclose(() => setConnection("offline"));
    let disposed = false;
    const start = () =>
      hub
        .start()
        .then(() => {
          if (!disposed) {
            setConnection("connected");
            setTick((n) => n + 1);
          }
        })
        .catch(() => {
          if (!disposed) setConnection("offline");
        });
    void start();
    const timer = setInterval(() => {
      setTick((n) => n + 1);
      if (hub.state === "Disconnected") void start();
    }, 5000);
    return () => {
      disposed = true;
      clearInterval(timer);
      void hub.stop();
    };
  }, [signed]);
  const login = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError("");
    try {
      if (setup?.required && password !== confirmation)
        throw new Error(t("Пароли не совпадают", "Passwords do not match"));
      await api(setup?.required ? "/setup" : "/login", { password });
      setSetup({ required: false });
      setConfirmation("");
      setPassword("");
      setSigned(true);
    } catch (e) {
      setError((e as Error).message);
      api<Data>("/setup")
        .then(setSetup)
        .catch(() => {});
    } finally {
      setBusy(false);
    }
  };
  const weakPassword =
    password.length > 0 &&
    (password.length < 8 ||
      /^(.)\1+$/.test(password) ||
      [
        "password",
        "password123",
        "12345678",
        "123456789",
        "qwerty123",
        "qwertyuiop",
      ].includes(password.toLowerCase()));
  if (signed === null || (!signed && setup === null))
    return (
      <main className="login">
        <h1>KommitEye</h1>
        <p>{error || t("Подключение к серверу…", "Connecting to server…")}</p>
      </main>
    );
  if (!signed)
    return (
      <main className="login">
        <div className="brand">
          KommitEye<span> / web server</span>
        </div>
        <h1>
          {setup?.required
            ? t("Создайте пароль", "Create your password")
            : t(
                "Проверки коммитов.\nВ одном месте.",
                "Commit reviews.\nOne workspace.",
              )}
        </h1>
        <p>
          {setup?.required
            ? setup.local
              ? t(
                  "Первый запуск: задайте пароль администратора сервера.",
                  "First launch: choose the server administrator password.",
                )
              : t(
                  "Для первоначальной настройки откройте localhost с портом сервера на самом сервере. Затем можно входить из локальной сети.",
                  "For initial setup, open localhost with the server port on the server computer. You can then sign in over the LAN.",
                )
            : t(
                "Войдите для управления проектами на этом сервере.",
                "Sign in to manage projects on this server.",
              )}
        </p>
        <form onSubmit={login}>
          <label className="field">
            <span>{t("Пароль администратора", "Administrator password")}</span>
            <input
              autoFocus
              type="password"
              autoComplete={
                setup?.required ? "new-password" : "current-password"
              }
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
          </label>
          {setup?.required && (
            <label className="field">
              <span>{t("Повторите пароль", "Confirm password")}</span>
              <input
                type="password"
                autoComplete="new-password"
                value={confirmation}
                onChange={(e) => setConfirmation(e.target.value)}
                required
              />
            </label>
          )}
          {setup?.required && weakPassword && (
            <p role="status" className="password-warning">
              {t(
                "Пароль очень слабый, его легко угадать. Лучше выбрать более длинный и непредсказуемый. Вы можете продолжить с этим паролем.",
                "This password is very weak and easy to guess. Consider a longer, less predictable password. You may still continue with this password.",
              )}
            </p>
          )}
          <button
            className="primary"
            disabled={busy || (setup?.required && !setup.local)}
          >
            {busy
              ? t("Подождите…", "Working…")
              : setup?.required
                ? t("Сохранить пароль и войти", "Save password and sign in")
                : t("Войти", "Sign in")}
          </button>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
        </form>
      </main>
    );
  return (
    <div className="shell">
      <aside>
        <div className="brand">
          KommitEye<span> / web server</span>
        </div>
        <nav>
          {[
            ["projects", "Проекты", "Projects"],
            ["reports", "Отчёты", "Reports"],
            ["jobs", "Задания", "Jobs"],
            ["models", "Модели", "Models"],
            ["mail", "Почта", "Mail"],
            ["prompt", "Промпт", "Prompt"],
          ].map(([key, ru, en]) => (
            <button
              key={key}
              aria-current={tab === key ? "page" : undefined}
              onClick={() => setTab(key)}
            >
              {t(ru, en)}
            </button>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <span className={"connection " + connection}>
            {connection === "connected"
              ? t("Сервер подключён", "Server connected")
              : t("Восстановление связи", "Reconnecting")}
          </span>
          <small>
            {info.machine} · v{info.version}
          </small>
          <select
            aria-label="Language"
            value={lang}
            onChange={(e) => {
              setLanguage(e.target.value);
              setLang(e.target.value);
            }}
          >
            <option value="ru">Русский</option>
            <option value="en">English</option>
          </select>
          <Action
            onClick={async () => {
              await api("/logout", {});
              setSigned(false);
            }}
          >
            {t("Выйти", "Sign out")}
          </Action>
        </div>
      </aside>
      <main className="workspace" key={lang}>
        {tab === "projects" ? (
          <Projects tick={tick} />
        ) : tab === "reports" ? (
          <Reports tick={tick} />
        ) : tab === "jobs" ? (
          <Jobs tick={tick} />
        ) : tab === "models" ? (
          <Models />
        ) : tab === "mail" ? (
          <Mail />
        ) : (
          <Prompt />
        )}
      </main>
    </div>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
