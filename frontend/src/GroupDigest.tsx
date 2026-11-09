import { useEffect, useState } from "react";

type Digest = {
  chatId: string;
  groupTitle: string;
  date: string;
  timeZone: string;
  text: string;
  newTasks: unknown[];
  blockers: unknown[];
  risks: unknown[];
  eventCount: number;
};

type Props = {
  chatId: string;
  apiUrl: string;
  headers: Record<string, string>;
  revision: string;
};

export function GroupDigest({ chatId, apiUrl, headers, revision }: Props) {
  const [date, setDate] = useState("");
  const [digest, setDigest] = useState<Digest | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    const load = async () => {
      setLoading(true);
      setError(null);
      try {
        const query = date ? `?date=${encodeURIComponent(date)}` : "";
        const response = await fetch(`${apiUrl}/api/groups/${encodeURIComponent(chatId)}/digest${query}`, {
          headers, signal: controller.signal,
        });
        if (!response.ok) throw new Error(response.status === 404
          ? "Группа не найдена или у вас нет доступа." : `Не удалось загрузить дайджест (${response.status}).`);
        const data: Digest = await response.json();
        if (!controller.signal.aborted) setDigest(data);
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Ошибка загрузки дайджеста.");
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    };
    void load();
    // A notification cancels an older request so a stale response cannot overwrite new data.
    return () => controller.abort();
  }, [chatId, apiUrl, headers, date, revision]);

  return (
    <section className="digest" aria-labelledby="digest-heading" aria-busy={loading}>
      <div className="digest-heading">
        <div>
          <h2 id="digest-heading">Дайджест группы</h2>
          <p className="subtitle">Новые задачи, блокеры и риски за выбранный день</p>
        </div>
        <label className="digest-date">
          Дата
          <input type="date" value={date || digest?.date || ""} onChange={(e) => setDate(e.target.value)} />
        </label>
      </div>
      {error && <div className="error" role="alert">{error}</div>}
      {digest && !error && <>
        <div className="digest-counts">
          <span>Новые задачи: <strong>{digest.newTasks.length}</strong></span>
          <span>Блокеры: <strong>{digest.blockers.length}</strong></span>
          <span>Риски: <strong>{digest.risks.length}</strong></span>
        </div>
        <pre className="digest-text">{digest.text}</pre>
        <p className="digest-note">Часовой пояс: {digest.timeZone}. {loading ? "Обновляем…" : "Данные обновляются автоматически."}</p>
      </>}
      {!digest && loading && <p role="status">Загрузка дайджеста…</p>}
    </section>
  );
}
