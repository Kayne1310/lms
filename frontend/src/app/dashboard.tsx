"use client";

import { useCallback, useEffect, useMemo, useState, useSyncExternalStore } from "react";
import type { FormEvent, ReactNode } from "react";
import { Avatar, Button, ConfigProvider, Dropdown, Input, InputNumber, Modal, Select, Spin, Switch, Table, Tag, message } from "antd";
import { LogoutOutlined, ReloadOutlined } from "@ant-design/icons";
import dynamic from "next/dynamic";
import type { ColumnsType } from "antd/es/table";
import viVN from "antd/locale/vi_VN";
import dayjs, { type Dayjs } from "dayjs";
import utc from "dayjs/plugin/utc";
import timezone from "dayjs/plugin/timezone";
import LearningProgress from "./learning-progress";

dayjs.extend(utc);
dayjs.extend(timezone);
const ScheduleDatePicker = dynamic(() => import("./schedule-date-picker"), { ssr: false, loading: () => <Input placeholder="Chọn ngày giờ chạy" readOnly /> });

const api = process.env.NEXT_PUBLIC_API_BASE_URL ?? "/api";
const tokenStorageKey = "utc-attendance.access-token";

type Attempt = { id?: string; status: string; message: string; createdAtUtc: string; detailsJson?: string | null };
type Session = {
  id: string;
  title: string;
  scheduledAtUtc: string;
  durationMinutes: number;
  isActive: boolean;
  isEnded: boolean;
  remoteAttended: boolean;
  isManualJob: boolean;
  isQueued: boolean;
  jobScheduledAtUtc?: string | null;
  lastAttempt?: Attempt;
};
type Course = {
  id: string;
  code: string;
  name: string;
  color?: string;
  currentLesson?: string;
  documentCount: number;
  autoAttendanceEnabled: boolean;
  sessions: Session[];
};
type Dashboard = {
  configured: boolean;
  account?: { email: string; autoAttendanceEnabled: boolean; attendanceLeadTimeMinutes: number; lastSyncedAtUtc?: string };
  courses?: Course[];
  history?: (Attempt & { id: string; sessionTitle: string; courseName: string })[];
};
type IconName = "overview" | "sessions" | "courses" | "history" | "learning" | "sync" | "chevron";
type View = "overview" | "sessions" | "courses" | "history" | "learning";

const vietnamTime = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});
const dateOnly = new Intl.DateTimeFormat("vi-VN", {
  weekday: "short",
  day: "2-digit",
  month: "2-digit",
  timeZone: "Asia/Ho_Chi_Minh",
});

function Icon({ name }: { name: IconName }) {
  const paths: Record<IconName, ReactNode> = {
    overview: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
    sessions: <><rect x="3" y="4" width="18" height="17" rx="2" /><path d="M8 2v4M16 2v4M3 9h18M7 13h4M7 17h7" /></>,
    courses: <><rect x="4" y="5" width="16" height="16" rx="2" /><path d="M8 3v4M16 3v4M4 10h16M8 14h3M8 17h6" /></>,
    history: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2M4 5 2.5 8M4 5l3 .5" /></>,
    learning: <><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v17H6.5A2.5 2.5 0 0 1 4 17.5z" /><path d="M4 6h13M8 10h8M8 14h6M4 17h13" /></>,
    sync: <><path d="M20 7v5h-5M4 17v-5h5" /><path d="M5.6 9A7 7 0 0 1 18 6l2 2M4 16l2 2a7 7 0 0 0 12.4-3" /></>,
    chevron: <path d="m9 18 6-6-6-6" />,
  };

  return <svg aria-hidden="true" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">{paths[name]}</svg>;
}

function parseDetails(value?: string | null) {
  if (!value) return null;
  try { return JSON.parse(value); } catch { return { raw: value }; }
}

function statusFor(session: Session) {
  if (session.remoteAttended) return ["Đã điểm danh", "success"] as const;
  if (session.isActive) return ["Đang live", "live"] as const;
  if (session.isEnded) return ["Đã kết thúc", "muted"] as const;
  if (session.isQueued) return ["Đã lên lịch", "queued"] as const;
  return ["Chờ đồng bộ", "muted"] as const;
}

function attemptLabel(status: string) {
  if (status === "success" || status === "already_recorded") return "Thành công";
  if (status === "not_live") return "Chưa live";
  if (status === "not_found") return "Không tìm thấy buổi";
  if (status === "disabled") return "Đã tắt tự động";
  if (status === "error") return "Lỗi gọi API";
  return status === "rejected" ? "Bị từ chối" : status;
}

function manualActionLabel(session: Session) {
  if (session.isQueued) return "Đang xử lý";
  return session.remoteAttended ? "Gửi lại" : "Điểm danh";
}

export default function Home() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [authMode, setAuthMode] = useState<"login" | "register">("login");
  const [, setSessionVersion] = useState(0);
  const [busy, setBusy] = useState(false);
  const [loadError, setLoadError] = useState(false);
  const [activeView, setActiveView] = useState<View>("overview");
  const [sessionCourseId, setSessionCourseId] = useState("all");
  const [sessionStateFilter, setSessionStateFilter] = useState("all");
  const [sessionLimit, setSessionLimit] = useState(10);
  const [historyCourseName, setHistoryCourseName] = useState("all");
  const [historyStatus, setHistoryStatus] = useState("all");
  const [historyLimit, setHistoryLimit] = useState(20);
  const [sessionPage, setSessionPage] = useState(1);
  const [historyPage, setHistoryPage] = useState(1);
  const [selectedAttempt, setSelectedAttempt] = useState<(Attempt & { id: string; sessionTitle: string; courseName: string }) | null>(null);
  const [scheduleSession, setScheduleSession] = useState<(Session & { course: Course }) | null>(null);
  const [scheduleValue, setScheduleValue] = useState<Dayjs | null>(null);
  const [leadTimeDraft, setLeadTimeDraft] = useState<number | null>(null);
  const [savingSchedule, setSavingSchedule] = useState(false);
  const [mounted, setMounted] = useState(false);
  const [messageApi, messageContext] = message.useMessage();

  useEffect(() => {
    setMounted(true);
  }, []);

  const subscribeToSession = useCallback((onStoreChange: () => void) => {
    const onStorage = (event: StorageEvent) => { if (event.key === tokenStorageKey) onStoreChange(); };
    window.addEventListener("storage", onStorage);
    return () => window.removeEventListener("storage", onStorage);
  }, []);
  const readSessionToken = useCallback(() => {
    if (typeof window === "undefined") return null;
    return window.localStorage.getItem(tokenStorageKey) ?? window.sessionStorage.getItem(tokenStorageKey);
  }, []);
  const token = useSyncExternalStore(subscribeToSession, readSessionToken, () => null);
  const saveToken = useCallback((value: string | null) => {
    if (typeof window !== "undefined") {
      if (value) {
        window.localStorage.setItem(tokenStorageKey, value);
        window.sessionStorage.setItem(tokenStorageKey, value);
      } else {
        window.localStorage.removeItem(tokenStorageKey);
        window.sessionStorage.removeItem(tokenStorageKey);
      }
    }
    setSessionVersion((version) => version + 1);
  }, []);

  const load = useCallback(async (accessToken = token) => {
    if (!accessToken) return;
    const response = await fetch(`${api}/dashboard`, { cache: "no-store", headers: { Authorization: `Bearer ${accessToken}` } });
    if (response.status === 401 || response.status === 403) {
      saveToken(null);
      setData(null);
      return;
    }
    if (!response.ok) throw new Error("Không tải được dữ liệu.");
    setData(await response.json());
    setLoadError(false);
  }, [token, saveToken]);

  const hasQueuedJobs = Boolean(data?.courses?.some((course) => course.sessions.some((session) => session.isQueued)));

  useEffect(() => {
    if (!token) return;
    const refresh = () => load().catch(() => setLoadError(true));
    refresh();
    const timer = window.setInterval(refresh, hasQueuedJobs ? 2000 : 15000);
    return () => window.clearInterval(timer);
  }, [token, load, hasQueuedJobs]);

  useEffect(() => {
    const readView = () => {
      const view = window.location.hash.slice(1);
      setActiveView(["overview", "sessions", "courses", "history", "learning"].includes(view) ? view as View : "overview");
    };
    readView();
    window.addEventListener("hashchange", readView);
    return () => window.removeEventListener("hashchange", readView);
  }, []);

  useEffect(() => {
    if (!selectedAttempt) return;
    const closeOnEscape = (event: KeyboardEvent) => { if (event.key === "Escape") setSelectedAttempt(null); };
    window.addEventListener("keydown", closeOnEscape);
    return () => window.removeEventListener("keydown", closeOnEscape);
  }, [selectedAttempt]);

  const run = async (path: string, init?: RequestInit, success = "Đã cập nhật.", refresh = true) => {
    if (!token) return;
    const toastKey = `dashboard:${path}`;
    setBusy(true);
    messageApi.open({ key: toastKey, type: "loading", content: "Đang xử lý…", duration: 0 });
    try {
      const response = await fetch(`${api}/dashboard${path}`, {
        ...init,
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}`, ...(init?.headers ?? {}) },
      });
      if (!response.ok) {
        const payload = await response.json().catch(() => ({}));
        throw new Error(payload.message || "Không thực hiện được. Thử lại sau.");
      }
      messageApi.open({ key: toastKey, type: "success", content: success, duration: 2.5 });
      if (refresh) await load();
      else void load().catch(() => setLoadError(true));
    } catch (error) {
      messageApi.open({ key: toastKey, type: "error", content: error instanceof Error ? error.message : "Có lỗi xảy ra.", duration: 4 });
    } finally {
      setBusy(false);
    }
  };

  const authenticate = (event: FormEvent) => {
    event.preventDefault();
    const toastKey = "auth";
    setBusy(true);
    messageApi.open({ key: toastKey, type: "loading", content: "Đang xác thực với Eduso…", duration: 0 });
    void fetch(`${api}/auth/${authMode}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ email, password: password || null }),
    }).then(async (response) => {
      const payload = await response.json().catch(() => ({}));
      if (!response.ok || !payload.token) throw new Error(payload.message || "Không thể xác thực tài khoản.");
      saveToken(payload.token);
      setPassword("");
      setData(null);
      await load(payload.token);
      messageApi.open({ key: toastKey, type: "success", content: authMode === "register" ? "Đăng ký thành công. Đang đồng bộ dữ liệu…" : "Đăng nhập thành công.", duration: 2.5 });
    }).catch((error) => {
      messageApi.open({ key: toastKey, type: "error", content: error instanceof Error ? error.message : "Có lỗi xảy ra.", duration: 4 });
    }).finally(() => setBusy(false));
  };

  const courses = useMemo(() => data?.courses ?? [], [data?.courses]);
  const allSessions = useMemo(
    () => courses.flatMap((course) => course.sessions.map((session) => ({ ...session, course })))
      .sort((a, b) => +new Date(a.scheduledAtUtc) - +new Date(b.scheduledAtUtc)),
    [courses],
  );
  const upcoming = allSessions.filter((session) => !session.isEnded);
  const filteredSessions = allSessions.filter((session) => {
    const matchesCourse = sessionCourseId === "all" || session.course.id === sessionCourseId;
    const matchesState = sessionStateFilter === "all"
      || (sessionStateFilter === "upcoming" ? !session.isEnded : session.remoteAttended);
    return matchesCourse && matchesState;
  });
  const sessionsPerPage = Number(sessionLimit);
  const sessionPages = Math.max(1, Math.ceil(filteredSessions.length / sessionsPerPage));
  const currentSessionPage = Math.min(sessionPage, sessionPages);
  const history = data?.history ?? [];
  const filteredHistory = history.filter((item) => {
    const matchesCourse = historyCourseName === "all" || item.courseName === historyCourseName;
    const succeeded = item.status === "success" || item.status === "already_recorded";
    const matchesStatus = historyStatus === "all" || (historyStatus === "success" ? succeeded : !succeeded);
    return matchesCourse && matchesStatus;
  });
  const historyPerPage = Number(historyLimit);
  const historyPages = Math.max(1, Math.ceil(filteredHistory.length / historyPerPage));
  const currentHistoryPage = Math.min(historyPage, historyPages);
  const pageTitle: Record<View, string> = { overview: "Tổng quan", sessions: "Buổi điểm danh", courses: "Môn học", history: "Lịch sử điểm danh", learning: "Bài tập & LP" };
  const navigate = (view: View) => setActiveView(view);
  const showAttendedSessions = () => { setSessionStateFilter("attended"); navigate("sessions"); };
  const logout = async () => {
    const toastKey = "dashboard:logout";
    messageApi.open({ key: toastKey, type: "loading", content: "Đang đăng xuất…", duration: 0 });
    try {
      const response = await fetch(`${api}/dashboard/logout`, { method: "POST", headers: { Authorization: `Bearer ${token}` } });
      if (!response.ok) throw new Error("Không đăng xuất được. Thử lại sau.");
      saveToken(null);
      setData(null);
      messageApi.open({ key: toastKey, type: "success", content: "Đã đăng xuất. Lịch tự động vẫn tiếp tục chạy.", duration: 3 });
    } catch (error) {
      messageApi.open({ key: toastKey, type: "error", content: error instanceof Error ? error.message : "Không đăng xuất được.", duration: 4 });
    }
  };

  const openScheduleEditor = (session: Session & { course: Course }) => {
    const suggested = session.jobScheduledAtUtc ?? new Date(new Date(session.scheduledAtUtc).getTime() - (account.attendanceLeadTimeMinutes ?? 5) * 60_000).toISOString();
    setScheduleSession(session);
    setScheduleValue(dayjs.utc(suggested).tz("Asia/Ho_Chi_Minh"));
  };
  const saveSchedule = async () => {
    if (!scheduleSession || !scheduleValue) return;
    setSavingSchedule(true);
    try {
      const jobScheduledAtUtc = dayjs.tz(scheduleValue.format("YYYY-MM-DD HH:mm:ss"), "YYYY-MM-DD HH:mm:ss", "Asia/Ho_Chi_Minh").utc().toISOString();
      const response = await fetch(`${api}/dashboard/sessions/${scheduleSession.id}/schedule`, {
        method: "PATCH", headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` }, body: JSON.stringify({ jobScheduledAtUtc }),
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || "Không lưu được giờ chạy.");
      messageApi.success("Đã cập nhật giờ chạy.");
      setScheduleSession(null);
      await load();
    } catch (error) {
      messageApi.error(error instanceof Error ? error.message : "Không lưu được giờ chạy.");
    } finally { setSavingSchedule(false); }
  };

  if (!mounted) {
    return (
      <ConfigProvider locale={viVN}>
        <main className="message-page">
          <Spin size="large" aria-label="Đang nạp phiên đăng nhập..." />
        </main>
      </ConfigProvider>
    );
  }

  if (!token) {
    return (
      <ConfigProvider locale={viVN} theme={{ token: { colorPrimary: "#bd4a08", colorText: "#102744", borderRadius: 7, fontFamily: "inherit" } }}>
        <main className="login-page">
          {messageContext}
          <section className="login-panel">
            <div className="brand-lockup"><span className="brand-mark">U</span><span>UTC Attend<small>Đào tạo</small></span></div>
            <h1>{authMode === "login" ? "Đăng nhập" : "Tạo tài khoản"}</h1>
            <p className="login-copy">Nhập thông tin tài khoản Eduso để xác thực.</p>
            <div className="auth-switch" role="tablist" aria-label="Chọn hình thức xác thực">
              <Button type={authMode === "login" ? "primary" : "default"} onClick={() => setAuthMode("login")} role="tab" aria-selected={authMode === "login"}>Đăng nhập</Button>
              <Button type={authMode === "register" ? "primary" : "default"} onClick={() => setAuthMode("register")} role="tab" aria-selected={authMode === "register"}>Đăng ký</Button>
            </div>
            <form onSubmit={authenticate}>
              <label htmlFor="email">Email UTC</label>
              <Input id="email" type="email" autoComplete="email" required value={email} onChange={(event) => setEmail(event.target.value)} placeholder="mssv@lms.utc.edu.vn" size="large" />
              <label htmlFor="password">Mật khẩu Eduso</label>
              <Input.Password id="password" autoComplete="current-password" required value={password} onChange={(event) => setPassword(event.target.value)} placeholder="Nhập mật khẩu Eduso" size="large" />
              <Button type="primary" htmlType="submit" loading={busy} size="large">{authMode === "login" ? "Đăng nhập" : "Kiểm tra và đăng ký"}</Button>
            </form>
            <p className="notice"></p>
          </section>
        </main>
      </ConfigProvider>
    );
  }

  if (!data && loadError) {
    return <ConfigProvider locale={viVN}><main className="message-page"><section><h1>Mất kết nối API</h1><Button type="primary" onClick={() => void load().catch(() => setLoadError(true))}>Thử lại</Button></section></main></ConfigProvider>;
  }
  if (!data) return <ConfigProvider locale={viVN}><main className="message-page"><Spin size="large" aria-label="Đang tải" /></main></ConfigProvider>;

  const account = data.account!;
  const nextSession = upcoming[0];
  const sessionColumns: ColumnsType<Session & { course: Course }> = [
    { title: "Buổi học", dataIndex: "title", key: "title", render: (_, session) => <div className="primary-cell"><strong>{session.title}</strong><small>{dateOnly.format(new Date(session.scheduledAtUtc))}</small></div> },
    { title: "Môn", key: "course", render: (_, session) => <div className="session-course"><strong>{session.course.name}</strong><small>{session.course.code}</small></div> },
    { title: "Thời gian", dataIndex: "scheduledAtUtc", key: "scheduledAtUtc", render: (value: string) => vietnamTime.format(new Date(value)) },
    { title: "Trạng thái / giờ job", key: "status", render: (_, session) => { const [label, tone] = statusFor(session); return <div className="status-cell"><Tag bordered={false} className={`status-label ${tone}`}>{label}</Tag><small>{session.isQueued && session.jobScheduledAtUtc ? `Job ${vietnamTime.format(new Date(session.jobScheduledAtUtc))}` : "Chưa lên lịch"}</small></div>; } },
    { title: "Thao tác", key: "actions", align: "right", width: 300, onCell: () => ({ style: { width: 300, minWidth: 300 } }), render: (_, session) => <div className="table-actions"><Button style={{ width: 128, minWidth: 128 }} onClick={() => openScheduleEditor(session)} disabled={session.isEnded || session.remoteAttended || session.isManualJob || !account.autoAttendanceEnabled || !session.course.autoAttendanceEnabled}>Giờ chạy</Button><Button style={{ width: 128, minWidth: 128 }} type="primary" ghost onClick={() => void run(`/sessions/${session.id}/attend`, { method: "POST" }, "Đã xếp job.", false)} disabled={busy || session.isQueued}>{manualActionLabel(session)}</Button></div> },
  ];
  const historyColumns: ColumnsType<(Attempt & { id: string; sessionTitle: string; courseName: string })> = [
    { title: "Buổi học", dataIndex: "sessionTitle", key: "sessionTitle", render: (value: string) => <strong>{value}</strong> },
    { title: "Môn", dataIndex: "courseName", key: "courseName" },
    { title: "Kết quả", dataIndex: "status", key: "status", render: (value: string) => <Tag bordered={false} className={`status-label ${value === "success" || value === "already_recorded" ? "success" : "muted"}`}>{attemptLabel(value)}</Tag> },
    { title: "Thời gian", dataIndex: "createdAtUtc", key: "createdAtUtc", render: (value: string) => vietnamTime.format(new Date(value)) },
    { title: "", key: "details", align: "right", render: (_, item) => <Button onClick={() => setSelectedAttempt(item)}>Chi tiết</Button> },
  ];
  const courseColumns: ColumnsType<Course> = [
    { title: "Môn", key: "course", render: (_, course) => <div className="course-name"><span className="course-color" style={{ backgroundColor: course.color || "#c24b08" }} /><div><strong>{course.name}</strong><small>{course.code}</small></div></div> },
    { title: "Bài hiện tại", dataIndex: "currentLesson", key: "currentLesson", render: (value?: string) => value || "—" },
    { title: "Live gần nhất", key: "latest", render: (_, course) => { const latest = course.sessions.slice().sort((a, b) => +new Date(b.scheduledAtUtc) - +new Date(a.scheduledAtUtc))[0]; return latest ? vietnamTime.format(new Date(latest.scheduledAtUtc)) : "—"; } },
    { title: "Tự động", dataIndex: "autoAttendanceEnabled", key: "autoAttendanceEnabled", align: "center", render: (enabled: boolean, course) => <Switch checked={enabled} onChange={(checked) => void run(`/courses/${course.id}/automation`, { method: "PATCH", body: JSON.stringify({ enabled: checked }) })} disabled={busy} aria-label={`Tự động điểm danh ${course.name}`} /> },
  ];

  return (
    <ConfigProvider locale={viVN} theme={{ token: { colorPrimary: "#bd4a08", colorLink: "#bd4a08", borderRadius: 7, controlHeight: 40, fontFamily: "inherit", colorText: "#102744", colorBorder: "#d6e0eb" }, components: { Table: { headerBg: "#f9fbfd", headerColor: "#536983", rowHoverBg: "#fff8f2", borderColor: "#dce4ed" }, Select: { optionSelectedBg: "#fff3e9" }, DatePicker: { cellActiveWithRangeBg: "#fff3e9" } } }}>
      {messageContext}
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-lockup"><span className="brand-mark">U</span><span>UTC Attend<small>Đào tạo</small></span></div>
        <nav className="side-menu" aria-label="Điều hướng chính">
          {([["overview", "Tổng quan", "overview"], ["sessions", "Buổi điểm danh", "sessions"], ["courses", "Môn học", "courses"], ["learning", "Bài tập & LP", "learning"], ["history", "Lịch sử", "history"]] as const).map(([view, label, icon]) => <Button key={view} href={`#${view}`} type="text" className={`side-menu-item ${activeView === view ? "active" : ""}`} onClick={() => navigate(view)}><Icon name={icon} />{label}</Button>)}
        </nav>
        <div className="sidebar-bottom">
          <div className="automation-row">
            <span className={`state-dot ${account.autoAttendanceEnabled ? "on" : "off"}`} />
            <div><strong>Tự động</strong><small>{account.autoAttendanceEnabled ? "Đang bật" : "Đang tắt"}</small></div>
            <Switch checked={account.autoAttendanceEnabled} onChange={(enabled) => void run("/automation", { method: "PATCH", body: JSON.stringify({ enabled }) })} disabled={busy} aria-label={account.autoAttendanceEnabled ? "Tạm dừng tự động điểm danh" : "Bật tự động điểm danh"} />
          </div>
        </div>
      </aside>

      <div className="main-column">
        <header className="topbar">
          <div className="breadcrumb"><span>UTC Attend</span><b>/</b><strong>{pageTitle[activeView]}</strong></div>
          <div className="topbar-actions"><span className="clock-label">Giờ Việt Nam</span><Button className="sync-button" type="primary" icon={<ReloadOutlined />} onClick={() => void run("/sync", { method: "POST" }, "Đã xếp đồng bộ vào hàng đợi.")} loading={busy}>Đồng bộ</Button><Dropdown trigger={["click"]} menu={{ items: [{ key: "email", label: account.email, disabled: true }, { type: "divider" }, { key: "logout", label: "Đăng xuất", icon: <LogoutOutlined />, danger: true }], onClick: ({ key }) => {
            if (key === "logout") Modal.confirm({ title: "Đăng xuất?", content: "Bạn sẽ thoát tài khoản này. Tự động điểm danh và các lịch đã lên vẫn tiếp tục chạy.", okText: "Đăng xuất", cancelText: "Ở lại", onOk: logout });
          } }}>
            <Button className="account-menu" shape="circle" icon={<Avatar size={28}>{account.email.slice(0, 1).toUpperCase()}</Avatar>} aria-label={`Tài khoản ${account.email}`} title="Tài khoản" />
          </Dropdown></div>
        </header>

        <main className="workspace">
          <div className="page-heading"><h1>{pageTitle[activeView]}</h1></div>

          {activeView === "overview" && <>
            <section className="summary-strip" aria-label="Tóm tắt">
              <div><span>Môn học</span><strong>{courses.length}</strong></div>
              <div><span>Buổi sắp tới</span><strong>{upcoming.length}</strong></div>
              <div><span>Đồng bộ gần nhất</span><strong className="summary-time">{account.lastSyncedAtUtc ? vietnamTime.format(new Date(account.lastSyncedAtUtc)) : "Chưa có"}</strong></div>
              <div className="next-session"><span>Buổi gần nhất</span><strong>{nextSession ? vietnamTime.format(new Date(nextSession.scheduledAtUtc)) : "Chưa có lịch"}</strong><small>{nextSession ? `${nextSession.course.name} · ${nextSession.title}` : ""}</small></div>
            </section>
            <section className="schedule-config" aria-labelledby="schedule-config-title">
              <div><h2 id="schedule-config-title">Lịch tự động</h2><p>Job chạy trước giờ học; chỉ gửi khi Eduso báo đang live.</p></div>
              <label className="filter-field">Chạy trước giờ học (phút)<InputNumber min={0} max={60} step={1} value={leadTimeDraft ?? account.attendanceLeadTimeMinutes} disabled={busy} addonAfter="phút" onChange={(value) => setLeadTimeDraft(value)} /></label>
              <Button type="primary" onClick={() => { if (leadTimeDraft !== null) void run("/automation/schedule", { method: "PATCH", body: JSON.stringify({ leadTimeMinutes: leadTimeDraft }) }, "Đã lưu lịch job.").then(() => setLeadTimeDraft(null)); }} disabled={busy || leadTimeDraft === null}>Lưu lịch</Button>
            </section>
            <section className="content-section" aria-labelledby="overview-sessions-title">
              <div className="section-heading"><h2 id="overview-sessions-title">Sắp diễn ra</h2><Button type="link" onClick={() => navigate("sessions")}>Xem tất cả <Icon name="chevron" /></Button></div>
              {upcoming.length ? <Table className="antd-data-table" rowKey="id" columns={sessionColumns} dataSource={upcoming.slice(0, 5)} pagination={false} size="middle" scroll={{ x: 850 }} /> : <div className="empty-state">Chưa có buổi sắp tới.</div>}
            </section>
            {history.length > 0 && <section className="content-section"><div className="section-heading"><h2>Điểm danh gần đây</h2><Button type="link" onClick={() => navigate("history")}>Mở lịch sử <Icon name="chevron" /></Button></div>
              <Table className="antd-data-table" rowKey="id" columns={historyColumns.filter((column) => column.key !== "details")} dataSource={history.slice(0, 5)} pagination={false} size="middle" scroll={{ x: 650 }} />
            </section>}
          </>}

          {activeView === "sessions" && <section className="content-section view-section" aria-labelledby="sessions-title">
            <div className="section-heading"><div><h2 id="sessions-title">Lịch các buổi học</h2><p className="section-caption">{filteredSessions.length} buổi phù hợp</p></div>
              <div className="filter-bar">
                <label className="filter-field">Trạng thái<Select value={sessionStateFilter} onChange={(value) => { setSessionStateFilter(value); setSessionPage(1); }} options={[{ value: "upcoming", label: "Buổi chưa kết thúc" }, { value: "attended", label: "Đã điểm danh" }, { value: "all", label: "Tất cả buổi" }]} /></label>
                <label className="filter-field">Môn học<Select value={sessionCourseId} onChange={(value) => { setSessionCourseId(value); setSessionPage(1); }} options={[{ value: "all", label: "Tất cả môn" }, ...courses.map((course) => ({ value: course.id, label: course.name }))]} showSearch optionFilterProp="label" /></label>
                <label className="filter-field">Số buổi<Select value={sessionLimit} onChange={(value) => { setSessionLimit(value); setSessionPage(1); }} options={[10, 20, 50].map((value) => ({ value, label: `${value} buổi` }))} /></label>
              </div>
            </div>
            {filteredSessions.length ? <Table className="antd-data-table" rowKey="id" columns={sessionColumns} dataSource={filteredSessions} size="middle" scroll={{ x: 980 }} pagination={{ current: currentSessionPage, pageSize: sessionsPerPage, total: filteredSessions.length, showSizeChanger: false, showTotal: (total, range) => `${range[0]}–${range[1]} / ${total} buổi`, onChange: setSessionPage }} /> : <div className="empty-state">{sessionStateFilter === "attended" ? "Chưa có buổi nào được Eduso ghi nhận đã điểm danh." : "Không có buổi nào phù hợp với bộ lọc."}</div>}
          </section>}

          {activeView === "courses" && <section className="content-section view-section" aria-labelledby="courses-title">
            <div className="section-heading"><div><h2 id="courses-title">Danh sách môn học</h2><p className="section-caption">Bật hoặc tắt tự động theo từng môn</p></div><span>{courses.length} môn</span></div>
            {courses.length ? <Table className="antd-data-table" rowKey="id" columns={courseColumns} dataSource={courses} pagination={false} size="middle" scroll={{ x: 720 }} /> : <div className="empty-state">Chưa có môn học.</div>}
          </section>}

          {activeView === "learning" && <LearningProgress token={token} />}

          {activeView === "history" && <section className="content-section view-section" aria-labelledby="history-title">
            <div className="section-heading"><div><h2 id="history-title">Các lần điểm danh</h2><p className="section-caption">{filteredHistory.length} yêu cầu do ứng dụng gửi · tối đa 100 bản ghi gần nhất</p></div>
              <div className="filter-bar">
                <label className="filter-field">Môn học<Select value={historyCourseName} onChange={(value) => { setHistoryCourseName(value); setHistoryPage(1); }} options={[{ value: "all", label: "Tất cả môn" }, ...Array.from(new Set(history.map((item) => item.courseName))).map((name) => ({ value: name, label: name }))]} showSearch optionFilterProp="label" /></label>
                <label className="filter-field">Kết quả<Select value={historyStatus} onChange={(value) => { setHistoryStatus(value); setHistoryPage(1); }} options={[{ value: "all", label: "Tất cả" }, { value: "success", label: "Thành công" }, { value: "failed", label: "Chưa thành công" }]} /></label>
                <label className="filter-field">Hiển thị<Select value={historyLimit} onChange={(value) => { setHistoryLimit(value); setHistoryPage(1); }} options={[10, 20, 50].map((value) => ({ value, label: `${value} dòng` }))} /></label>
              </div>
            </div>
            {filteredHistory.length ? <Table className="antd-data-table" rowKey="id" columns={historyColumns} dataSource={filteredHistory} size="middle" scroll={{ x: 800 }} pagination={{ current: currentHistoryPage, pageSize: historyPerPage, total: filteredHistory.length, showSizeChanger: false, showTotal: (total, range) => `${range[0]}–${range[1]} / ${total} dòng`, onChange: setHistoryPage }} /> : history.length === 0 && historyStatus === "all" && historyCourseName === "all" ? <div className="empty-state history-empty">Chưa có request điểm danh từ ứng dụng. <Button type="link" onClick={showAttendedSessions}>Xem buổi Eduso đã ghi nhận <Icon name="chevron" /></Button></div> : <div className="empty-state">Không có lịch sử phù hợp với bộ lọc.</div>}
          </section>}
        </main>
      </div>
      <Modal open={!!selectedAttempt} title="Chi tiết điểm danh" onCancel={() => setSelectedAttempt(null)} footer={<Button onClick={() => setSelectedAttempt(null)}>Đóng</Button>} width={760} destroyOnClose>
        {selectedAttempt && <><p className="modal-subtitle">{selectedAttempt.sessionTitle} · {selectedAttempt.courseName} · {vietnamTime.format(new Date(selectedAttempt.createdAtUtc))}</p><div className="json-result"><Tag bordered={false} className={`status-label ${selectedAttempt.status === "success" || selectedAttempt.status === "already_recorded" ? "success" : "muted"}`}>{attemptLabel(selectedAttempt.status)}</Tag><span>{selectedAttempt.message}</span></div><pre className="json-preview">{JSON.stringify(parseDetails(selectedAttempt.detailsJson) ?? { status: selectedAttempt.status, message: selectedAttempt.message }, null, 2)}</pre></>}
      </Modal>
      <Modal title={`Sửa giờ chạy${scheduleSession ? ` · ${scheduleSession.course.name}` : ""}`} open={!!scheduleSession} onCancel={() => setScheduleSession(null)} onOk={() => void saveSchedule()} confirmLoading={savingSchedule} okText="Lưu giờ chạy" cancelText="Hủy">
        {scheduleSession && <div className="schedule-editor"><p>{scheduleSession.title} · Bắt đầu {vietnamTime.format(new Date(scheduleSession.scheduledAtUtc))}</p><ScheduleDatePicker value={scheduleValue} onChange={setScheduleValue} /></div>}
      </Modal>
    </div>
    </ConfigProvider>
  );
}
