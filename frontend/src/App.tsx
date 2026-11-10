import { useCallback, useEffect, useMemo, useState } from "react";
import {
  HubConnectionBuilder,
  HubConnectionState,
  type HubConnection,
} from "@microsoft/signalr";
import { GroupDigest } from "./GroupDigest";

const API_URL = import.meta.env.VITE_API_URL ?? "";

type Group = {
  chatId: string;
  title: string;
  username: string | null;
  memberCount: number | null;
  status: string;
  updatedAtUtc: string;
};

type TelegramWebApp = {
  initData: string;
  version: string;
  ready?: () => void;
  expand?: () => void;
  requestChat?: (requestId: string, callback?: (sent: boolean) => void) => void;
  isVersionAtLeast?: (version: string) => boolean;
};

declare global {
  interface Window {
    Telegram?: { WebApp: TelegramWebApp };
  }
}

function mergeGroup(groups: Group[], incoming: Group): Group[] {
  const index = groups.findIndex((group) => group.chatId === incoming.chatId);
  if (index === -1) {
    return [...groups, incoming].sort((a, b) => a.title.localeCompare(b.title));
  }

  const current = groups[index];
  if (new Date(incoming.updatedAtUtc) < new Date(current.updatedAtUtc)) return groups;

  const next = [...groups];
  next[index] = incoming;
  return next.sort((a, b) => a.title.localeCompare(b.title));
}

export default function App() {
  const telegram = window.Telegram?.WebApp;
  const initData = telegram?.initData ?? "";
  const canUseRequestChat = Boolean(
    initData && telegram?.requestChat && (telegram.isVersionAtLeast?.("9.6") ?? true),
  );

  const authHeaders = useMemo<Record<string, string>>(
    (): Record<string, string> => (initData ? { "X-Telegram-Init-Data": initData } : {}),
    [initData],
  );

  const [groups, setGroups] = useState<Group[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [hubState, setHubState] = useState("Подключение...");
  const [preparedButtonId, setPreparedButtonId] = useState<string | null>(null);
  const [preparingButton, setPreparingButton] = useState(false);
  const [addMessage, setAddMessage] = useState<string | null>(null);
  const [selectedChatId, setSelectedChatId] = useState<string | null>(null);
  const [digestRevisions, setDigestRevisions] = useState<Record<string, number>>({});
  const [connectionEpoch, setConnectionEpoch] = useState(0);
  const [isDevelopmentBrowser, setIsDevelopmentBrowser] = useState(false);
  const [sessionLoaded, setSessionLoaded] = useState(false);

  useEffect(() => {
    setSelectedChatId((current) => groups.some((g) => g.chatId === current) ? current : groups[0]?.chatId ?? null);
  }, [groups]);

  const loadGroups = useCallback(async () => {
    try {
      setError(null);
      const response = await fetch(`${API_URL}/api/groups`, { headers: authHeaders });
      if (!response.ok) throw new Error(`API вернул ${response.status}`);
      const data: Group[] = await response.json();
      // Не затираем более свежие события, которые могли прийти через SignalR во время загрузки.
      setGroups((current) => data.reduce(mergeGroup, current));
    } catch (err) {
      setError(err instanceof Error ? err.message : "Не удалось загрузить группы");
    } finally {
      setLoading(false);
    }
  }, [authHeaders]);

  const prepareGroupRequest = useCallback(async () => {
    if (!canUseRequestChat) return;

    setPreparingButton(true);
    try {
      const response = await fetch(`${API_URL}/api/groups/connect-request`, {
        method: "POST",
        headers: authHeaders,
      });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.detail ?? `API вернул ${response.status}`);
      }
      const data: { preparedButtonId: string } = await response.json();
      setPreparedButtonId(data.preparedButtonId);
    } catch (err) {
      setAddMessage(err instanceof Error ? err.message : "Не удалось подготовить подключение");
      setPreparedButtonId(null);
    } finally {
      setPreparingButton(false);
    }
  }, [authHeaders, canUseRequestChat]);

  useEffect(() => {
    let connection: HubConnection | undefined;
    let disposed = false;
    setSessionLoaded(false);

    const loadSession = async () => {
      try {
        const response = await fetch(`${API_URL}/api/auth/me`, { headers: authHeaders });
        if (!response.ok) throw new Error("Could not load session");
        const session: { isDevelopment: boolean } = await response.json();
        if (!disposed) setIsDevelopmentBrowser(session.isDevelopment && !initData);
      } catch {
        if (!disposed) setIsDevelopmentBrowser(false);
      } finally {
        if (!disposed) setSessionLoaded(true);
      }
    };

    const start = async () => {
      telegram?.ready?.();
      telegram?.expand?.();

      const hubUrl = new URL(`${API_URL}/hubs/groups`, window.location.origin);
      if (initData) hubUrl.searchParams.set("initData", initData);

      connection = new HubConnectionBuilder()
        .withUrl(hubUrl.toString())
        .withAutomaticReconnect()
        .build();

      connection.on("GroupUpdated", (group: Group) => {
        setGroups((current) => mergeGroup(current, group));
      });
      connection.on("DigestUpdated", (message: { chatId: string; date: string }) => {
        setDigestRevisions((current) => ({ ...current, [message.chatId]: (current[message.chatId] ?? 0) + 1 }));
      });
      connection.onreconnecting(() => setHubState("Переподключение..."));
      connection.onreconnected(() => {
        if (disposed) return;
        setHubState("Онлайн");
        setConnectionEpoch((current) => current + 1);
        void loadGroups();
      });
      connection.onclose(() => setHubState("Нет соединения"));

      try {
        await connection.start();
        if (!disposed) {
          setHubState("Онлайн");
          setConnectionEpoch((current) => current + 1);
          void loadGroups();
        }
      } catch {
        if (!disposed) setHubState("Нет соединения");
      }
    };

    void loadSession();
    void loadGroups();
    void start();
    void prepareGroupRequest();

    return () => {
      disposed = true;
      if (connection?.state !== HubConnectionState.Disconnected) void connection?.stop();
    };
  }, [initData, authHeaders, loadGroups, prepareGroupRequest]);

  // requestChat вызывается непосредственно из click handler: нативное окно открывает Telegram.
  const addGroup = () => {
    if (!telegram?.requestChat || !preparedButtonId) return;

    const currentButtonId = preparedButtonId;
    setPreparedButtonId(null);
    setAddMessage("Ожидаю подтверждения выбора группы в Telegram...");

    telegram.requestChat(currentButtonId, (sent) => {
      setAddMessage(
        sent
          ? "Выбор отправлен. Группа появится здесь после обработки события."
          : "Выбор группы отменён.",
      );
      void prepareGroupRequest();
    });
  };

  return (
    <main className="page">
      <header className="header">
        <div>
          <p className="eyebrow">ПАНЕЛЬ УПРАВЛЕНИЯ</p>
          <h1>Мои группы</h1>
          <p className="subtitle">Ваши группы и ежедневные дайджесты команды</p>
        </div>
        <div className={`connection ${hubState === "Онлайн" ? "online" : "offline"}`}>
          <span className="dot" /> {hubState}
        </div>
      </header>

      <section className="toolbar">
        <div>
          <strong>{groups.length}</strong> подключённых групп
        </div>
        <div className="toolbar-actions">
          <button className="secondary-button" onClick={() => {
            void loadGroups();
            setConnectionEpoch((current) => current + 1);
          }} disabled={loading}>
            Обновить
          </button>
          {!isDevelopmentBrowser && <button onClick={addGroup} disabled={!canUseRequestChat || !preparedButtonId || preparingButton}>
            {preparingButton ? "Подготовка..." : "＋ Добавить группу"}
          </button>}
        </div>
      </section>

      {sessionLoaded && !canUseRequestChat && !isDevelopmentBrowser && (
        <div className="hint">
          Откройте Mini App внутри Telegram на актуальной версии приложения, чтобы подключать группы.
        </div>
      )}
      {addMessage && <div className="hint">{addMessage}</div>}
      {error && <div className="error">{error}</div>}

      {loading ? (
        <div className="empty">Загрузка ваших групп...</div>
      ) : groups.length === 0 ? (
        <div className="empty">{isDevelopmentBrowser
          ? "Для этой учётной записи пока нет подключённых групп."
          : "У вас пока нет подключённых групп. Нажмите «Добавить группу»."}</div>
      ) : (
        <section className="groups">
          {groups.map((group) => (
            <article className="group-card" key={group.chatId}>
              <div className="group-icon">#</div>
              <div className="group-info">
                <h2>{group.title}</h2>
                <p>{group.username ? `@${group.username}` : group.chatId}</p>
                <div className="meta">
                  <span>{group.memberCount ?? "—"} участников</span>
                  <span>Обновлено: {new Date(group.updatedAtUtc).toLocaleString()}</span>
                </div>
              </div>
              <span className={`status ${group.status === "connected" ? "status-active" : ""}`}>
                {group.status}
              </span>
            </article>
          ))}
        </section>
      )}
      {groups.length > 0 && <div className="digest-selector">
        <label htmlFor="digest-group">Дайджест для группы</label>
        <select id="digest-group" value={selectedChatId ?? ""} onChange={(e) => setSelectedChatId(e.target.value)}>
          {groups.map((group) => <option key={group.chatId} value={group.chatId}>{group.title}</option>)}
        </select>
      </div>}
      {selectedChatId && <GroupDigest key={selectedChatId} chatId={selectedChatId} apiUrl={API_URL}
        headers={authHeaders} revision={`${connectionEpoch}:${digestRevisions[selectedChatId] ?? 0}`} />}
    </main>
  );
}
