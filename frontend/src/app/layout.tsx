import type { Metadata } from "next";
import "antd/dist/reset.css";
import "./globals.css";

export const metadata: Metadata = {
  title: "UTC Attend | Tổng quan",
  description: "Theo dõi lịch học và điểm danh UTC.",
};

export default function RootLayout({ children }: LayoutProps<"/">) {
  return (
    <html lang="vi" className="h-full">
      <body className="min-h-full">{children}</body>
    </html>
  );
}
