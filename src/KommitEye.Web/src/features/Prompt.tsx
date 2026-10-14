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
export function Prompt() {
  const [data, setData] = useState<Data | null>(null),
    [message, setMessage] = useState(""),
    [promptLanguage, setPromptLanguage] = useState(language);
  useEffect(() => {
    api("/prompt?language=" + promptLanguage)
      .then(setData)
      .catch((e) => setMessage(e.message));
  }, [promptLanguage]);
  return (
    <>
      <Heading
        title={t("Системный промпт", "System prompt")}
        description={t(
          "Изменения используются в следующих запросах модели.",
          "Changes apply to subsequent model requests.",
        )}
      />
      {data && (
        <section className="panel">
          <label className="field">
            <span>{t("Язык промпта", "Prompt language")}</span>
            <select
              value={promptLanguage}
              onChange={(e) => setPromptLanguage(e.target.value)}
            >
              <option value="ru">Русский</option>
              <option value="en">English</option>
            </select>
          </label>
          <textarea
            aria-label="System prompt"
            rows={22}
            value={data.text}
            onChange={(e) => setData({ ...data, text: e.target.value })}
          />
          <Action
            onClick={async () => {
              await api("/prompt", data);
              setData(await api("/prompt?language=" + promptLanguage));
              setMessage(t("Сохранено", "Saved"));
            }}
          >
            {t("Сохранить", "Save")}
          </Action>
        </section>
      )}
      <p role="status">{message}</p>
    </>
  );
}
