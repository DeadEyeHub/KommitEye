import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api";
import { t, language, setLanguage, Heading } from "../ui";

type Finding = {
  id: number;
  file: string;
  line: number | null;
  description: string;
  status: string;
  comment: string;
  respondedAt: string | null;
};
type Review = {
  id: number;
  project: string;
  branch: string;
  sha: string;
  subject: string;
  savedAt: string | null;
  outcome: string;
  findingCount: number;
  pendingCount: number;
};
type Detail = { review: Review; findings: Finding[]; authorEmail: string };
const responseLabel = (status: string) =>
  status === "confirmed"
    ? t("Баг подтверждён", "Bug confirmed")
    : status === "false_positive"
      ? t("Ложное срабатывание", "False positive")
      : t("Ожидает ответа", "Awaiting response");
const outcomeLabel = (value: string) =>
  ({
    bug: t("Найдены проблемы", "Findings"),
    clean: t("Без замечаний", "No findings"),
    empty: t("Нет изменений", "Empty diff"),
    failed: t("Проверка не выполнена", "Review failed"),
    unknown: t("Результат не определён", "Unknown result"),
  })[value] || value;

export function AuthorPortal() {
  const [email, setEmail] = useState(""),
    [code, setCode] = useState(""),
    [user, setUser] = useState<string | null>(null);
  const [ready, setReady] = useState(false),
    [enabled, setEnabled] = useState(true),
    [sent, setSent] = useState(false);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [message, setMessage] = useState("");
  const [retryAt, setRetryAt] = useState(0),
    [now, setNow] = useState(Date.now()),
    [lang, setLang] = useState(language);
  const [items, setItems] = useState<Review[]>([]),
    [hasMore, setHasMore] = useState(false),
    [offset, setOffset] = useState(0),
    [filter, setFilter] = useState("");
  const [refresh, setRefresh] = useState(0),
    [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<number | null>(() => {
    const id = Number(new URLSearchParams(location.search).get("review"));
    return Number.isSafeInteger(id) && id > 0 ? id : null;
  });
  useEffect(() => {
    document.documentElement.lang = lang;
  }, [lang]);
  useEffect(() => {
    let live = true;
    Promise.all([
      api<{ enabled: boolean }>("/author/auth/info"),
      api<{ email: string }>("/author/session").catch(() => null),
    ])
      .then(([info, session]) => {
        if (live) {
          setEnabled(info.enabled);
          setUser(session?.email ?? null);
        }
      })
      .catch((e) => {
        if (live) setError(e.message);
      })
      .finally(() => {
        if (live) setReady(true);
      });
    const expired = () => {
      setUser(null);
    };
    window.addEventListener("session-expired", expired);
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => {
      live = false;
      clearInterval(timer);
      window.removeEventListener("session-expired", expired);
    };
  }, []);
  useEffect(() => {
    if (!user || selected) return;
    let live = true;
    setLoading(true);
    setError("");
    api<{ items: Review[]; hasMore: boolean }>(
      `/author/reviews?offset=${offset}&filter=${filter}`,
    )
      .then((page) => {
        if (live) {
          setItems(page.items);
          setHasMore(page.hasMore);
        }
      })
      .catch((e) => {
        if (live) setError(e.message);
      })
      .finally(() => {
        if (live) setLoading(false);
      });
    return () => {
      live = false;
    };
  }, [user, selected, offset, filter, refresh]);
  async function requestCode() {
    setBusy(true);
    setError("");
    setMessage("");
    try {
      const response = await api<{ resendSeconds: number }>(
        "/author/auth/request",
        { email },
      );
      setSent(true);
      setCode("");
      setRetryAt(Date.now() + response.resendSeconds * 1000);
      setMessage(
        t(
          "Если для этого адреса есть проверки и лимиты отправки не превышены, код отправлен. Проверьте почту и папку «Спам».",
          "If this address has reviews and sending limits allow it, a code has been sent. Check your inbox and spam folder.",
        ),
      );
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function submit(e: FormEvent) {
    e.preventDefault();
    if (!sent) {
      await requestCode();
      return;
    }
    setBusy(true);
    setError("");
    try {
      const session = await api<{ email: string }>("/author/auth/verify", {
        email,
        code,
      });
      setUser(session.email);
      setCode("");
      setMessage("");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  const languageMenu = (
    <label className="field language-menu">
      <span>Язык / Language</span>
      <select
        value={lang}
        onChange={(e) => {
          setLanguage(e.target.value);
          setLang(language);
        }}
      >
        <option value="ru">Русский</option>
        <option value="en">English</option>
      </select>
    </label>
  );
  if (!ready)
    return (
      <main className="login">
        <h1>KommitEye</h1>
        <p role="status">{t("Подключение…", "Connecting…")}</p>
      </main>
    );
  if (!user)
    return (
      <main className="login">
        <div className="brand">KommitEye</div>
        {languageMenu}
        <h1>{t("Мои коммиты", "My commits")}</h1>
        <p>
          {enabled
            ? t(
                "Войдите с почтой, указанной в ваших коммитах. Мы отправим одноразовый код.",
                "Sign in with the email used in your commits. We will send a one-time code.",
              )
            : t(
                "Кабинет автора отключён. Обратитесь к администратору.",
                "The author portal is disabled. Contact your administrator.",
              )}
        </p>
        {enabled && (
          <form onSubmit={submit}>
            <label className="field">
              <span>Email</span>
              <input
                type="email"
                required
                maxLength={254}
                autoComplete="email"
                value={email}
                disabled={busy || sent}
                onChange={(e) => setEmail(e.target.value)}
              />
            </label>
            {sent && (
              <label className="field">
                <span>{t("Код из письма", "Email code")}</span>
                <input
                  autoFocus
                  inputMode="numeric"
                  autoComplete="one-time-code"
                  pattern="[0-9]{6}"
                  maxLength={6}
                  required
                  value={code}
                  onChange={(e) => setCode(e.target.value)}
                />
              </label>
            )}
            <div className="buttons">
              <button className="primary" disabled={busy}>
                {busy
                  ? t("Подождите…", "Working…")
                  : sent
                    ? t("Войти", "Sign in")
                    : t("Получить код", "Send code")}
              </button>
              {sent && (
                <>
                  <button
                    type="button"
                    disabled={busy || now < retryAt}
                    onClick={() => void requestCode()}
                  >
                    {now < retryAt
                      ? t("Повторить через ", "Resend in ") +
                        Math.ceil((retryAt - now) / 1000) +
                        t(" с", " s")
                      : t("Отправить снова", "Resend code")}
                  </button>
                  <button
                    type="button"
                    disabled={busy}
                    onClick={() => {
                      setSent(false);
                      setCode("");
                      setMessage("");
                    }}
                  >
                    {t("Другой email", "Change email")}
                  </button>
                </>
              )}
            </div>
          </form>
        )}
        {message && <p role="status">{message}</p>}
        {error && (
          <p role="alert" className="error">
            {error}
          </p>
        )}
      </main>
    );
  return (
    <main className="author-workspace" key={lang}>
      <header className="author-header">
        <div className="brand">KommitEye</div>
        <span>{user}</span>
        {languageMenu}
        <button
          onClick={async () => {
            try {
              await api("/author/logout", {});
              setUser(null);
              setSent(false);
            } catch (e) {
              setError((e as Error).message);
            }
          }}
        >
          {t("Выйти", "Sign out")}
        </button>
      </header>
      {selected ? (
        <>
          <button
            onClick={() => {
              setSelected(null);
              history.replaceState(null, "", "/author");
            }}
          >
            {t("К моим коммитам", "Back to my commits")}
          </button>
          <AuthorReviewView id={selected} />
        </>
      ) : (
        <>
          <Heading
            title={t("Мои коммиты", "My commits")}
            description={t(
              "Откройте проверку, подтвердите найденный баг или отметьте ложное срабатывание.",
              "Open a review to confirm a bug or mark a false positive.",
            )}
          />
          <div className="report-controls">
            <label>
              {t("Показать", "Show")}
              <select
                value={filter}
                onChange={(e) => {
                  setFilter(e.target.value);
                  setOffset(0);
                }}
              >
                <option value="">{t("Все коммиты", "All commits")}</option>
                <option value="pending">
                  {t("Ожидают ответа", "Awaiting response")}
                </option>
                <option value="bug">
                  {t("С проблемами", "With findings")}
                </option>
                <option value="clean">
                  {t("Без замечаний", "No findings")}
                </option>
              </select>
            </label>
            <button disabled={loading} onClick={() => setRefresh((n) => n + 1)}>
              {t("Обновить", "Refresh")}
            </button>
          </div>
          {loading ? (
            <p role="status">{t("Загрузка проверок…", "Loading reviews…")}</p>
          ) : !error && items.length === 0 ? (
            <p className="empty">
              {t(
                "Нет коммитов для выбранного фильтра.",
                "No commits match this filter.",
              )}
            </p>
          ) : (
            !error &&
            items.map((review) => (
              <article className="review-entry" key={review.id}>
                <div
                  className={"review-outcome review-outcome-" + review.outcome}
                >
                  <strong>{outcomeLabel(review.outcome)}</strong>
                  <span>
                    {t("Ожидают ответа: ", "Awaiting response: ")}
                    {review.pendingCount}
                  </span>
                </div>
                <div>
                  <h2>
                    <button
                      className="review-title-button"
                      onClick={() => {
                        setSelected(review.id);
                        history.replaceState(
                          null,
                          "",
                          "/author?review=" + review.id,
                        );
                      }}
                    >
                      <code>{review.sha.slice(0, 8)}</code> {review.subject}
                    </button>
                  </h2>
                  <p className="review-meta">
                    {review.project} · {review.branch}
                    {review.savedAt && (
                      <> · {new Date(review.savedAt).toLocaleString()}</>
                    )}
                  </p>
                  <span>
                    {t("Найдено проблем: ", "Findings: ")}
                    {review.findingCount}
                  </span>
                </div>
              </article>
            ))
          )}
          <nav
            className="report-pagination"
            aria-label={t("Страницы коммитов", "Commit pages")}
          >
            <button
              disabled={loading || offset === 0}
              onClick={() => setOffset((n) => Math.max(0, n - 50))}
            >
              {t("Назад", "Previous")}
            </button>
            <span>
              {t("Страница ", "Page ")}
              {offset / 50 + 1}
            </span>
            <button
              disabled={loading || !hasMore}
              onClick={() => setOffset((n) => n + 50)}
            >
              {t("Далее", "Next")}
            </button>
          </nav>
        </>
      )}
      {error && (
        <p role="alert" className="error">
          {error}
        </p>
      )}
    </main>
  );
}

export function AuthorReviewView({
  id,
  admin = false,
}: {
  id: number;
  admin?: boolean;
}) {
  const [detail, setDetail] = useState<Detail | null>(null),
    [error, setError] = useState(""),
    [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let live = true;
    setDetail(null);
    setError("");
    api<Detail>(
      admin ? `/report-entries/${id}/feedback` : `/author/reviews/${id}`,
    )
      .then((data) => {
        if (live) setDetail(data);
      })
      .catch((e) => {
        if (live) setError(e.message);
      });
    return () => {
      live = false;
    };
  }, [id, admin, attempt]);
  if (error)
    return (
      <p className="error" role="alert">
        {error}{" "}
        <button onClick={() => setAttempt((n) => n + 1)}>
          {t("Повторить", "Retry")}
        </button>
      </p>
    );
  if (!detail) return <p role="status">{t("Загрузка…", "Loading…")}</p>;
  return (
    <section className="author-detail">
      <h2>
        <code>{detail.review.sha.slice(0, 8)}</code> {detail.review.subject}
      </h2>
      <p className="review-meta">
        {detail.review.project} · {detail.review.branch} ·{" "}
        {outcomeLabel(detail.review.outcome)}
        {admin && <> · {detail.authorEmail}</>}
      </p>
      {detail.findings.length === 0 && (
        <p>
          {t(
            "В этой проверке нет структурированных находок.",
            "This review has no structured findings.",
          )}
        </p>
      )}
      {detail.findings.map((finding) => (
        <FindingResponse
          key={`${id}-${finding.id}-${detail.review.savedAt}`}
          finding={finding}
          review={detail.review}
          admin={admin}
        />
      ))}
    </section>
  );
}

function FindingResponse({
  finding,
  review,
  admin,
}: {
  finding: Finding;
  review: Review;
  admin: boolean;
}) {
  const [status, setStatus] = useState(finding.status),
    [comment, setComment] = useState(finding.comment);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState(""),
    [saved, setSaved] = useState(false);
  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError("");
    setSaved(false);
    try {
      await api(`/author/reviews/${review.id}/findings/${finding.id}`, {
        status,
        comment,
        savedAt: review.savedAt,
      });
      setSaved(true);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <article className="author-finding">
      <h3>
        <code>
          {finding.file}
          {finding.line !== null && ":" + finding.line}
        </code>
      </h3>
      <p className="finding-description">{finding.description}</p>
      {admin ? (
        <>
          <strong>{responseLabel(finding.status)}</strong>
          {finding.comment && (
            <p className="finding-description">{finding.comment}</p>
          )}
          {finding.respondedAt && (
            <p className="muted">
              {new Date(finding.respondedAt).toLocaleString()}
            </p>
          )}
        </>
      ) : (
        <form onSubmit={save}>
          <label className="field">
            <span>{t("Ваш ответ", "Your response")}</span>
            <select
              disabled={busy}
              value={status}
              onChange={(e) => {
                setStatus(e.target.value);
                setSaved(false);
              }}
            >
              {["pending", "confirmed", "false_positive"].map((value) => (
                <option key={value} value={value}>
                  {responseLabel(value)}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            <span>
              {t("Комментарий (необязательно)", "Comment (optional)")}
            </span>
            <textarea
              disabled={busy}
              rows={3}
              maxLength={4000}
              value={comment}
              placeholder={t(
                "Например: это заглушка для будущей фичи",
                "For example: this is a placeholder for a future feature",
              )}
              onChange={(e) => {
                setComment(e.target.value);
                setSaved(false);
              }}
            />
          </label>
          <button className="primary" disabled={busy}>
            {busy
              ? t("Сохранение…", "Saving…")
              : t("Сохранить ответ", "Save response")}
          </button>
          {saved && (
            <p role="status">{t("Ответ сохранён", "Response saved")}</p>
          )}
          {error && (
            <p role="alert" className="error">
              {error}
            </p>
          )}
        </form>
      )}
    </article>
  );
}
