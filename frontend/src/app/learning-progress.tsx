"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Button, Empty, Modal, Progress, Skeleton, Table, Tag, message } from "antd";
import {
  CheckCircleOutlined,
  ClockCircleOutlined,
  CodeOutlined,
  CloseCircleOutlined,
  ExportOutlined,
  LoadingOutlined,
  ReloadOutlined,
  ThunderboltOutlined,
} from "@ant-design/icons";
import type { ColumnsType } from "antd/es/table";

const api = process.env.NEXT_PUBLIC_API_BASE_URL ?? "/api";
const tokenStorageKey = "utc-attendance.access-token";

type Lesson = {
  id: string;
  title: string;
  type?: string | null;
  weekNumber?: number | null;
  completionPct?: number | null;
  quizAttemptCount?: number | null;
  quizBestScore?: number | null;
  completed?: boolean | null;
  isLocked?: boolean | null;
};

type ProgressSnapshot = {
  id: string;
  courseId: string;
  courseCode: string;
  courseName: string;
  status: "success" | "error";
  message?: string | null;
  totalLp?: number | null;
  deltaLp?: number | null;
  totalCeu?: number | null;
  progressPct?: number | null;
  lpByType?: Record<string, number> | null;
  lessons?: Lesson[] | null;
  createdAtUtc: string;
};

type ProgressData = { latest: ProgressSnapshot[]; history: ProgressSnapshot[] };

type WorkflowLog = {
  time: string;
  tag: string;
  message: string;
  type: "info" | "work" | "success" | "warning" | "error" | "highlight";
};

type WorkflowState = {
  id: string;
  status: "running" | "completed" | "failed";
  progressPct: number;
  currentStep: string;
  currentCourseName?: string;
  logs: WorkflowLog[];
  docsCompleted: number;
  reflectionsSubmitted: number;
  postsPublished: number;
  quizzesSolved: number;
  totalLpGained: number;
  startedAt: string;
  finishedAt?: string;
  errorMessage?: string;
};

const timeFormatter = new Intl.DateTimeFormat("vi-VN", {
  dateStyle: "short",
  timeStyle: "short",
  timeZone: "Asia/Ho_Chi_Minh",
});

function formatPoints(value?: number | null) {
  return value === null || value === undefined
    ? "Chưa có dữ liệu"
    : `${new Intl.NumberFormat("vi-VN", { maximumFractionDigits: 2 }).format(value)} LP`;
}

function statusTag(snapshot: ProgressSnapshot) {
  return (
    <Tag bordered={false} className={`status-label ${snapshot.status === "success" ? "success" : "error"}`}>
      {snapshot.status === "success" ? "Đã đọc từ Eduso" : "Lỗi đồng bộ"}
    </Tag>
  );
}

export default function LearningProgress({ token }: { token: string }) {
  const [data, setData] = useState<ProgressData | null>(null);
  const [loading, setLoading] = useState(true);
  const [syncing, setSyncing] = useState(false);
  const [harvestingId, setHarvestingId] = useState<string | null>(null);
  const [harvestingAll, setHarvestingAll] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  // Workflow Live Terminal State
  const [workflow, setWorkflow] = useState<WorkflowState | null>(null);
  const [workflowModalVisible, setWorkflowModalVisible] = useState(false);
  const terminalLogsEndRef = useRef<HTMLDivElement>(null);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      const response = await fetch(`${api}/learning-progress`, {
        cache: "no-store",
        signal,
        headers: { Authorization: `Bearer ${token}` },
      });
      if (response.status === 401 || response.status === 403) {
        window.localStorage.removeItem(tokenStorageKey);
        window.sessionStorage.removeItem(tokenStorageKey);
        window.location.reload();
        return;
      }
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || "Không tải được lịch sử tiến độ.");
      setData(body as ProgressData);
      setError(null);
    },
    [token]
  );

  // Load ban đầu
  useEffect(() => {
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void load(controller.signal)
        .catch((reason) => {
          if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : "Không tải được tiến độ.");
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, 0);
    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [load]);

  // Kiểm tra xem có workflow active đang chạy không khi vào trang
  useEffect(() => {
    void fetch(`${api}/learning-progress/workflow/active`, {
      headers: { Authorization: `Bearer ${token}` },
    })
      .then((res) => res.json())
      .then((body) => {
        if (body?.active && body?.state) {
          setWorkflow(body.state as WorkflowState);
          setWorkflowModalVisible(true);
        }
      })
      .catch(() => {});
  }, [token]);

  // Polling workflow status khi đang running
  useEffect(() => {
    if (!workflow || workflow.status !== "running") return;

    const interval = setInterval(async () => {
      try {
        const res = await fetch(`${api}/learning-progress/workflow/${workflow.id}`, {
          headers: { Authorization: `Bearer ${token}` },
        });
        if (res.ok) {
          const updated: WorkflowState = await res.json();
          setWorkflow(updated);
          if (updated.status === "completed") {
            message.success("Workflow cày LP hoàn tất xuất sắc!");
            void load();
          } else if (updated.status === "failed") {
            message.error(updated.errorMessage || "Workflow cày LP thất bại.");
          }
        }
      } catch {
        /* bỏ qua lỗi mạng tạm thời trong lúc polling */
      }
    }, 1000);

    return () => clearInterval(interval);
  }, [workflow?.id, workflow?.status, token, load]);

  // Auto-scroll terminal log xuống cuối
  useEffect(() => {
    if (workflowModalVisible) {
      terminalLogsEndRef.current?.scrollIntoView({ behavior: "smooth" });
    }
  }, [workflow?.logs?.length, workflowModalVisible]);

  const sync = async () => {
    setSyncing(true);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch(`${api}/learning-progress/sync`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || "Không đọc được tiến độ từ Eduso.");
      setData(body as ProgressData);
      const failures = (body.latest as ProgressSnapshot[] | undefined)?.filter((item) => item.status === "error").length ?? 0;
      setNotice(failures ? `Đã lưu lịch sử; ${failures} môn chưa đọc được dữ liệu.` : "Đã cập nhật trạng thái và LP từ Eduso.");
    } catch (reason) {
      const messageText = reason instanceof Error ? reason.message : "Không đọc được tiến độ từ Eduso.";
      try {
        await load();
      } catch {
        /* Keep sync error */
      }
      setError(messageText);
    } finally {
      setSyncing(false);
    }
  };

  const harvestCourse = async (courseId: string, courseName: string) => {
    setHarvestingId(courseId);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch(`${api}/learning-progress/harvest/${courseId}`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || `Lỗi khi cày LP cho môn ${courseName}.`);

      if (body.state) {
        setWorkflow(body.state as WorkflowState);
        setWorkflowModalVisible(true);
      }
      setNotice(`Đã khởi động workflow cày LP cho môn ${courseName}.`);
    } catch (reason) {
      const messageText = reason instanceof Error ? reason.message : `Lỗi khi cày LP cho môn ${courseName}.`;
      setError(messageText);
    } finally {
      setHarvestingId(null);
    }
  };

  const harvestAll = async () => {
    setHarvestingAll(true);
    setError(null);
    setNotice(null);
    try {
      const response = await fetch(`${api}/learning-progress/harvest-all`, {
        method: "POST",
        headers: { Authorization: `Bearer ${token}` },
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.message || "Lỗi khi tự động cày LP tất cả các môn.");

      if (body.state) {
        setWorkflow(body.state as WorkflowState);
        setWorkflowModalVisible(true);
      }
      setNotice("Đã kích hoạt workflow tự động cày LP cho toàn bộ môn học.");
    } catch (reason) {
      const messageText = reason instanceof Error ? reason.message : "Lỗi khi cày LP tất cả các môn.";
      setError(messageText);
    } finally {
      setHarvestingAll(false);
    }
  };

  const latest = data?.latest ?? [];
  const history = data?.history ?? [];
  const successful = latest.filter((item) => item.status === "success");
  const totalLp = successful.reduce((sum, item) => sum + (item.totalLp ?? 0), 0);
  const lessonCount = successful.reduce((sum, item) => sum + (item.lessons?.length ?? 0), 0);
  const newestCheck = history[0]?.createdAtUtc;
  const lastChecked = newestCheck ? timeFormatter.format(new Date(newestCheck)) : "Chưa kiểm tra";

  const lessonColumns: ColumnsType<Lesson> = [
    {
      title: "Bài học",
      dataIndex: "title",
      key: "title",
      render: (title: string, lesson) => (
        <div className="primary-cell">
          <strong>{title}</strong>
          <small>
            {lesson.type || "Bài học"}
            {lesson.weekNumber ? ` · Tuần ${lesson.weekNumber}` : ""}
          </small>
        </div>
      ),
    },
    {
      title: "Hoàn thành",
      dataIndex: "completionPct",
      key: "completionPct",
      width: 160,
      render: (value?: number | null) =>
        value === null || value === undefined ? "—" : <Progress percent={Math.max(0, Math.min(100, Math.round(value)))} size="small" />,
    },
    {
      title: "Quiz",
      key: "quiz",
      width: 170,
      render: (_, lesson) =>
        lesson.quizAttemptCount
          ? `${lesson.quizAttemptCount} lượt · điểm cao nhất ${lesson.quizBestScore ?? "—"}`
          : "Chưa có lượt làm",
    },
    {
      title: "Trạng thái",
      key: "state",
      width: 140,
      render: (_, lesson) => (
        <Tag bordered={false} className={`status-label ${lesson.isLocked ? "muted" : lesson.completed ? "success" : "queued"}`}>
          {lesson.isLocked ? "Đang khóa" : lesson.completed ? "Hoàn thành" : "Đang học"}
        </Tag>
      ),
    },
  ];

  const courseColumns: ColumnsType<ProgressSnapshot> = [
    {
      title: "Môn học",
      key: "course",
      render: (_, item) => (
        <div className="primary-cell">
          <strong>{item.courseName}</strong>
          <small>{item.courseCode}</small>
        </div>
      ),
    },
    {
      title: "Tiến độ",
      dataIndex: "progressPct",
      key: "progressPct",
      width: 180,
      render: (value?: number | null) =>
        value === null || value === undefined ? "—" : <Progress percent={Math.max(0, Math.min(100, Math.round(value)))} size="small" />,
    },
    {
      title: "Tổng LP",
      dataIndex: "totalLp",
      key: "totalLp",
      width: 130,
      render: (value?: number | null) => <strong>{formatPoints(value)}</strong>,
    },
    {
      title: "Theo loại",
      key: "lpByType",
      render: (_, item) => {
        const entries = Object.entries(item.lpByType ?? {}) as [string, number][];
        return entries.length ? (
          <div className="lp-type-list">
            {entries.map(([type, value]) => (
              <Tag key={type}>
                {type}: {value} LP
              </Tag>
            ))}
          </div>
        ) : (
          "—"
        );
      },
    },
    {
      title: "Kiểm tra gần nhất",
      dataIndex: "createdAtUtc",
      key: "createdAtUtc",
      width: 155,
      render: (value: string) => timeFormatter.format(new Date(value)),
    },
    {
      title: "Trạng thái",
      key: "status",
      width: 130,
      render: (_, item) => statusTag(item),
    },
    {
      title: "Hành động",
      key: "actions",
      width: 160,
      render: (_, item) => (
        <Button
          size="small"
          type="primary"
          style={{ background: "#ea580c", borderColor: "#ea580c" }}
          icon={<ThunderboltOutlined />}
          loading={harvestingId === item.courseId || (workflow?.status === "running" && workflow?.currentCourseName === item.courseName)}
          disabled={syncing || harvestingAll}
          onClick={() => void harvestCourse(item.courseId, item.courseName)}
        >
          Cày LP môn này
        </Button>
      ),
    },
  ];

  const historyColumns: ColumnsType<ProgressSnapshot> = [
    {
      title: "Thời điểm",
      dataIndex: "createdAtUtc",
      key: "createdAtUtc",
      width: 165,
      render: (value: string) => timeFormatter.format(new Date(value)),
    },
    {
      title: "Môn học",
      key: "course",
      render: (_, item) => (
        <div className="primary-cell">
          <strong>{item.courseName}</strong>
          <small>{item.courseCode}</small>
        </div>
      ),
    },
    {
      title: "LP xác nhận",
      dataIndex: "totalLp",
      key: "totalLp",
      width: 150,
      render: (value?: number | null) => formatPoints(value),
    },
    {
      title: "Thay đổi từ lần trước",
      dataIndex: "deltaLp",
      key: "deltaLp",
      width: 190,
      render: (value?: number | null) =>
        value === null || value === undefined ? "Chưa có mốc so sánh" : `${value > 0 ? "+" : ""}${value} LP`,
    },
    {
      title: "Trạng thái",
      key: "status",
      width: 150,
      render: (_, item) => statusTag(item),
    },
    {
      title: "Ghi chú",
      dataIndex: "message",
      key: "message",
      render: (value?: string | null) => value || "—",
    },
  ];

  const getLogColor = (type: string) => {
    switch (type) {
      case "highlight":
        return "#fbbf24";
      case "success":
        return "#4ade80";
      case "work":
        return "#38bdf8";
      case "warning":
        return "#facc15";
      case "error":
        return "#f87171";
      default:
        return "#cbd5e1";
    }
  };

  const getTagColor = (tag: string) => {
    switch (tag) {
      case "LP1":
        return "#0284c7";
      case "QUIZ-DOC":
        return "#7c3aed";
      case "QUIZ-LESSON":
        return "#b91c1c";
      case "LP4":
        return "#9333ea";
      case "LP5":
        return "#d97706";
      case "EXAM":
        return "#dc2626";
      case "AUTH":
        return "#059669";
      case "COURSE":
        return "#ca8a04";
      default:
        return "#475569";
    }
  };

  return (
    <div className="learning-progress-page">
      <section className="learning-toolbar" aria-label="Cập nhật tiến độ">
        <div>
          <h2>Tiến độ bài học và Tự động làm bài tập (LP)</h2>
          <p>
            Theo dõi tiến độ LP và tự động đọc tài liệu (LP1), nộp nhật ký phản xạ (LP4), thảo luận (LP5) và giải bài kiểm tra tuần (LP2).
          </p>
        </div>
        <div className="learning-toolbar-actions">
          <span className="learning-last-checked">Cập nhật gần nhất: {lastChecked}</span>
          {workflow && (
            <Button
              type="dashed"
              style={{ borderColor: workflow.status === "running" ? "#22c55e" : "#64748b", color: workflow.status === "running" ? "#22c55e" : undefined }}
              icon={<CodeOutlined />}
              onClick={() => setWorkflowModalVisible(true)}
            >
              {workflow.status === "running" ? "Xem Live Workflow" : "Xem Nhật ký Cày LP"}
            </Button>
          )}
          <Button
            type="primary"
            style={{ background: "#ea580c", borderColor: "#ea580c" }}
            icon={<ThunderboltOutlined />}
            loading={harvestingAll || (workflow?.status === "running" && !workflow?.currentCourseName)}
            disabled={loading || syncing || Boolean(harvestingId)}
            onClick={() => void harvestAll()}
          >
            Tự động cày LP tất cả môn
          </Button>
          <Button
            icon={<ReloadOutlined />}
            loading={syncing}
            disabled={loading || harvestingAll || Boolean(harvestingId) || workflow?.status === "running"}
            onClick={() => void sync()}
          >
            Kiểm tra tiến độ
          </Button>
          <Button href="https://trolyai.eduso.vn/" target="_blank" rel="noreferrer" icon={<ExportOutlined />}>
            Mở Eduso
          </Button>
        </div>
      </section>

      {error && (
        <Alert
          className="learning-alert"
          type="error"
          showIcon
          message="Không cập nhật được tiến độ"
          description={error}
          action={
            <Button size="small" onClick={() => void load().catch((reason) => setError(reason instanceof Error ? reason.message : "Không tải được lịch sử."))}>
              Thử lại
            </Button>
          }
        />
      )}
      {notice && <Alert className="learning-alert" type="success" showIcon message={notice} closable onClose={() => setNotice(null)} />}

      <section className="summary-strip learning-summary" aria-label="Tóm tắt tiến độ">
        <div>
          <span>Môn đã đồng bộ</span>
          <strong>{successful.length}</strong>
        </div>
        <div>
          <span>Tổng LP theo môn</span>
          <strong>{formatPoints(totalLp)}</strong>
        </div>
        <div>
          <span>Bài trong kế hoạch</span>
          <strong>{lessonCount}</strong>
        </div>
        <div>
          <span>Lần kiểm tra gần nhất</span>
          <strong className="summary-time">{lastChecked}</strong>
        </div>
      </section>

      <section className="content-section view-section" aria-labelledby="learning-courses-title">
        <div className="section-heading">
          <div>
            <h2 id="learning-courses-title">Môn học và trạng thái từng bài</h2>
            <p className="section-caption">Điểm và LP được lấy từ dữ liệu Eduso; dữ liệu mới được lưu vào lịch sử khi bạn bấm kiểm tra.</p>
          </div>
        </div>
        {loading ? (
          <div className="learning-skeleton">
            <Skeleton active paragraph={{ rows: 4 }} />
          </div>
        ) : latest.length ? (
          <Table<ProgressSnapshot>
            className="antd-data-table"
            rowKey="id"
            columns={courseColumns}
            dataSource={latest}
            pagination={{ pageSize: 10, showSizeChanger: false }}
            scroll={{ x: 1050 }}
            expandable={{
              rowExpandable: (record) => record.status === "success" && Boolean(record.lessons?.length),
              expandedRowRender: (record) => (
                <Table<Lesson>
                  className="learning-lesson-table"
                  rowKey="id"
                  columns={lessonColumns}
                  dataSource={record.lessons ?? []}
                  pagination={false}
                  size="small"
                  scroll={{ x: 750 }}
                />
              ),
            }}
          />
        ) : (
          <div className="empty-state learning-empty">
            <Empty description="Chưa có dữ liệu tiến độ">
              <Button type="primary" loading={syncing} onClick={() => void sync()}>
                Kiểm tra tiến độ từ Eduso
              </Button>
            </Empty>
          </div>
        )}
      </section>

      <section className="content-section view-section" aria-labelledby="learning-history-title">
        <div className="section-heading">
          <div>
            <h2 id="learning-history-title">Lịch sử kiểm tra</h2>
            <p className="section-caption">Mỗi dòng là một snapshot đọc từ Eduso; mức thay đổi chỉ hiện khi có lần trước để đối chiếu.</p>
          </div>
          <span>{history.length} bản ghi gần nhất</span>
        </div>
        {loading ? (
          <div className="learning-skeleton">
            <Skeleton active paragraph={{ rows: 3 }} />
          </div>
        ) : history.length ? (
          <Table<ProgressSnapshot>
            className="antd-data-table"
            rowKey="id"
            columns={historyColumns}
            dataSource={history}
            size="middle"
            scroll={{ x: 900 }}
            pagination={{
              pageSize: 10,
              showSizeChanger: false,
              showTotal: (total, range) => `${range[0]}–${range[1]} / ${total} bản ghi`,
            }}
          />
        ) : (
          <div className="empty-state">Chưa có lịch sử. Bấm “Kiểm tra tiến độ” để lưu trạng thái hiện tại.</div>
        )}
      </section>

      {/* Cyberpunk Live Workflow Terminal Modal */}
      <Modal
        open={workflowModalVisible}
        onCancel={() => setWorkflowModalVisible(false)}
        width={880}
        footer={[
          <Button key="hide" onClick={() => setWorkflowModalVisible(false)}>
            {workflow?.status === "running" ? "Thu nhỏ (tiếp tục chạy nền)" : "Đóng"}
          </Button>,
          workflow?.status !== "running" && (
            <Button
              key="done"
              type="primary"
              style={{ background: "#16a34a", borderColor: "#16a34a" }}
              onClick={() => {
                setWorkflowModalVisible(false);
                void load();
              }}
            >
              Cập nhật dữ liệu bảng điểm
            </Button>
          ),
        ]}
        title={
          <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
            <ThunderboltOutlined style={{ color: "#ea580c", fontSize: 20 }} />
            <span style={{ fontSize: 16, fontWeight: 700, letterSpacing: "0.5px" }}>EDUSO AUTO LP WORKFLOW RUNNER</span>
            {workflow?.status === "running" && (
              <Tag color="processing" icon={<LoadingOutlined />} style={{ marginLeft: "auto", fontWeight: 600 }}>
                ĐANG CHẠY POLLING
              </Tag>
            )}
            {workflow?.status === "completed" && (
              <Tag color="success" icon={<CheckCircleOutlined />} style={{ marginLeft: "auto", fontWeight: 600 }}>
                HOÀN THÀNH
              </Tag>
            )}
            {workflow?.status === "failed" && (
              <Tag color="error" icon={<CloseCircleOutlined />} style={{ marginLeft: "auto", fontWeight: 600 }}>
                THẤT BẠI
              </Tag>
            )}
          </div>
        }
      >
        <div style={{ padding: "8px 0" }}>
          {/* Progress Section */}
          <div style={{ marginBottom: 16, background: "#f8fafc", padding: "12px 16px", borderRadius: 8, border: "1px solid #e2e8f0" }}>
            <div style={{ display: "flex", justifyContent: "space-between", marginBottom: 6, fontSize: 13 }}>
              <span>
                <strong>Bước hiện tại:</strong> <span style={{ color: "#2563eb" }}>{workflow?.currentStep || "Đang kết nối..."}</span>
              </span>
              <span>
                <strong>{workflow?.progressPct ?? 0}%</strong>
              </span>
            </div>
            <Progress
              percent={workflow?.progressPct ?? 0}
              status={workflow?.status === "failed" ? "exception" : workflow?.status === "completed" ? "success" : "active"}
              strokeColor={{ "0%": "#ea580c", "50%": "#0284c7", "100%": "#16a34a" }}
              showInfo={false}
            />
          </div>

          {/* Real-time Stat Badges */}
          <div
            style={{
              display: "grid",
              gridTemplateColumns: "repeat(5, 1fr)",
              gap: 8,
              marginBottom: 16,
              textAlign: "center",
            }}
          >
            <div style={{ background: "#f0fdf4", border: "1px solid #bbf7d0", padding: "8px 4px", borderRadius: 6 }}>
              <div style={{ fontSize: 11, color: "#166534" }}>📄 LP1 Tài liệu</div>
              <div style={{ fontSize: 18, fontWeight: 700, color: "#15803d" }}>{workflow?.docsCompleted ?? 0}</div>
            </div>
            <div style={{ background: "#faf5ff", border: "1px solid #e9d5ff", padding: "8px 4px", borderRadius: 6 }}>
              <div style={{ fontSize: 11, color: "#6b21a8" }}>📝 LP4 Nhật ký</div>
              <div style={{ fontSize: 18, fontWeight: 700, color: "#7e22ce" }}>{workflow?.reflectionsSubmitted ?? 0}</div>
            </div>
            <div style={{ background: "#fffbeb", border: "1px solid #fef3c7", padding: "8px 4px", borderRadius: 6 }}>
              <div style={{ fontSize: 11, color: "#92400e" }}>💬 LP5 Thảo luận</div>
              <div style={{ fontSize: 18, fontWeight: 700, color: "#b45309" }}>{workflow?.postsPublished ?? 0}</div>
            </div>
            <div style={{ background: "#fef2f2", border: "1px solid #fecaca", padding: "8px 4px", borderRadius: 6 }}>
              <div style={{ fontSize: 11, color: "#991b1b" }}>🎯 Quiz & Bài thi</div>
              <div style={{ fontSize: 18, fontWeight: 700, color: "#b91c1c" }}>{workflow?.quizzesSolved ?? 0}</div>
            </div>
            <div style={{ background: "#eff6ff", border: "1px solid #bfdbfe", padding: "8px 4px", borderRadius: 6 }}>
              <div style={{ fontSize: 11, color: "#1e40af" }}>⭐ Điểm LP Tăng</div>
              <div style={{ fontSize: 18, fontWeight: 700, color: "#1d4ed8" }}>+{workflow?.totalLpGained ?? 0}</div>
            </div>
          </div>

          {/* Hacker Terminal Screen */}
          <div
            style={{
              background: "#0d1117",
              color: "#c9d1d9",
              borderRadius: 8,
              border: "1px solid #30363d",
              boxShadow: "0 8px 24px rgba(0,0,0,0.5)",
              overflow: "hidden",
            }}
          >
            {/* Terminal Window Header Bar */}
            <div
              style={{
                background: "#161b22",
                padding: "8px 14px",
                display: "flex",
                alignItems: "center",
                borderBottom: "1px solid #21262d",
              }}
            >
              <div style={{ display: "flex", gap: 6, marginRight: 14 }}>
                <span style={{ width: 10, height: 10, borderRadius: "50%", background: "#ff5f56", display: "inline-block" }} />
                <span style={{ width: 10, height: 10, borderRadius: "50%", background: "#ffbd2e", display: "inline-block" }} />
                <span style={{ width: 10, height: 10, borderRadius: "50%", background: "#27c93f", display: "inline-block" }} />
              </div>
              <span style={{ fontSize: 12, color: "#8b949e", fontFamily: "Consolas, monospace" }}>
                root@utc-eduso-agent:~# live-tail -f workflow-{workflow?.id?.substring(0, 8) ?? "active"}.log
              </span>
              <span style={{ marginLeft: "auto", fontSize: 11, color: "#58a6ff" }}>
                <ClockCircleOutlined style={{ marginRight: 4 }} />
                {workflow?.startedAt ? new Date(workflow.startedAt).toLocaleTimeString("vi-VN") : "00:00:00"}
              </span>
            </div>

            {/* Terminal Content Body */}
            <div
              style={{
                padding: "12px 14px",
                fontFamily: "'Fira Code', 'Consolas', 'Monaco', monospace",
                fontSize: 12.5,
                lineHeight: "1.7",
                maxHeight: 350,
                overflowY: "auto",
              }}
            >
              {workflow?.logs && workflow.logs.length > 0 ? (
                workflow.logs.map((log, index) => (
                  <div key={index} style={{ display: "flex", alignItems: "flex-start", gap: 8, marginBottom: 2 }}>
                    <span style={{ color: "#6e7681", userSelect: "none", minWidth: 62 }}>[{log.time}]</span>
                    <span
                      style={{
                        background: getTagColor(log.tag),
                        color: "#fff",
                        padding: "0 5px",
                        borderRadius: 3,
                        fontSize: 10,
                        fontWeight: 700,
                        minWidth: 48,
                        textAlign: "center",
                        lineHeight: "18px",
                      }}
                    >
                      {log.tag}
                    </span>
                    <span style={{ color: getLogColor(log.type), wordBreak: "break-word", flex: 1 }}>{log.message}</span>
                  </div>
                ))
              ) : (
                <div style={{ color: "#8b949e" }}>Đang kết nối luồng socket và nạp dữ liệu workflow...</div>
              )}
              {workflow?.status === "running" && (
                <div style={{ display: "flex", alignItems: "center", gap: 6, marginTop: 6, color: "#38bdf8" }}>
                  <span style={{ animation: "pulse 1.5s infinite" }}>&gt;</span>
                  <span style={{ opacity: 0.7 }}>Đang thực thi các tiến trình tự động tiếp theo...</span>
                </div>
              )}
              <div ref={terminalLogsEndRef} />
            </div>
          </div>
        </div>
      </Modal>
    </div>
  );
}
