import { Suspense } from "react";
import Dashboard from "./dashboard";

export default function HomePage() {
  return <Suspense fallback={<main className="message-page"><p>Đang tải bảng điểm danh…</p></main>}><Dashboard /></Suspense>;
}
