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
export function Models() {
  const [data, setData] = useState<Data | null>(null),
    [index, setIndex] = useState(0),
    [message, setMessage] = useState(""),
    [available, setAvailable] = useState<Data[]>([]),
    [clearKeys, setClearKeys] = useState<string[]>([]);
  const load = () =>
    api("/models")
      .then(setData)
      .catch((e) => setMessage(e.message));
  useEffect(() => {
    void load();
  }, []);
  if (!data) return <p>{message || t("Загрузка…", "Loading…")}</p>;
  const profile = data.profiles[index]?.profile;
  const setProfile = (value: Data) =>
    setData({
      ...data,
      profiles: data.profiles.map((p: Data, i: number) =>
        i === index ? { ...p, profile: value } : p,
      ),
    });
  return (
    <>
      <Heading
        title={t("Модели", "Models")}
        description={t(
          "Адрес модели должен быть доступен с сервера KommitEye.",
          "The model endpoint must be reachable from the KommitEye server.",
        )}
      />
      <section className="panel">
        <div className="buttons">
          <select
            aria-label="Model profile"
            value={index}
            onChange={(e) => setIndex(Number(e.target.value))}
          >
            {data.profiles.map((p: Data, i: number) => (
              <option key={i} value={i}>
                {p.profile.name}
              </option>
            ))}
          </select>
          <button
            onClick={() => {
              const next = structuredClone(profile || { parameters: {} });
              next.name = "profile-" + (data.profiles.length + 1);
              next.apiKey = "";
              setData({
                ...data,
                profiles: [...data.profiles, { profile: next }],
              });
              setIndex(data.profiles.length);
            }}
          >
            {t("Добавить профиль", "Add profile")}
          </button>
        </div>
        {profile && (
          <>
            <div className="form-grid">
              <ObjectFields
                value={profile}
                set={setProfile}
                keys={[
                  "name",
                  "endpoint",
                  "model",
                  "apiKey",
                  "apiKeyEnvironment",
                ]}
              />
            </div>
            <p className="muted">
              {data.profiles[index].apiKeyConfigured
                ? t(
                    "Ключ сохранён. Пустое поле оставляет его без изменений.",
                    "Key saved. Leave blank to keep it.",
                  )
                : t("API-ключ не сохранён.", "No API key saved.")}
            </p>
            <label className="check">
              <input
                type="checkbox"
                checked={clearKeys.includes(profile.name)}
                onChange={(e) =>
                  setClearKeys(
                    e.target.checked
                      ? [...clearKeys, profile.name]
                      : clearKeys.filter((n) => n !== profile.name),
                  )
                }
              />
              {t("Удалить сохранённый API-ключ", "Clear saved API key")}
            </label>
            <div className="buttons">
              <Action
                onClick={async () =>
                  setAvailable(await api("/models/discover", profile))
                }
              >
                {t("Получить список моделей", "Discover models")}
              </Action>
              {available.length > 0 && (
                <select
                  aria-label="Available models"
                  value={profile.model}
                  onChange={(e) =>
                    setProfile({ ...profile, model: e.target.value })
                  }
                >
                  {available.map((m) => (
                    <option key={m.id}>{m.id}</option>
                  ))}
                </select>
              )}
            </div>
            <label className="check">
              <input
                type="checkbox"
                checked={data.activeProfile === profile.name}
                onChange={() =>
                  setData({ ...data, activeProfile: profile.name })
                }
              />
              {t("Активный профиль", "Active profile")}
            </label>
            <details>
              <summary>
                {t(
                  "Параметры запросов и лимиты",
                  "Request parameters and limits",
                )}
              </summary>
              <div className="form-grid">
                <ObjectFields
                  value={profile.parameters}
                  set={(parameters) => setProfile({ ...profile, parameters })}
                  keys={Object.keys(profile.parameters)}
                />
              </div>
            </details>
            <div className="buttons">
              <Action
                onClick={async () => {
                  await api("/models", {
                    revision: data.revision,
                    activeProfile: data.activeProfile,
                    profiles: data.profiles.map((p: Data) => p.profile),
                    clearKeys,
                  });
                  await load();
                  setClearKeys([]);
                  setMessage(t("Сохранено", "Saved"));
                }}
              >
                {t("Сохранить", "Save")}
              </Action>
              <Action
                onClick={async () => {
                  await api("/models/test", profile);
                  setMessage(
                    t("Модель отвечает", "Model connection successful"),
                  );
                }}
              >
                {t("Проверить подключение", "Test connection")}
              </Action>
            </div>
          </>
        )}
        <p role="status">{message}</p>
      </section>
    </>
  );
}
