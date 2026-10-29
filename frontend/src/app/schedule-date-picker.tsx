"use client";

import { DatePicker } from "antd";
import type { Dayjs } from "dayjs";

export default function ScheduleDatePicker({ value, onChange }: { value: Dayjs | null; onChange: (value: Dayjs | null) => void }) {
  return <DatePicker showTime={{ format: "HH:mm", minuteStep: 1 }} format="DD/MM/YYYY HH:mm" value={value} onChange={onChange} style={{ width: "100%" }} />;
}
