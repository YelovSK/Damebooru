import { ValidatorFn } from "@angular/forms";
import { Observable } from "rxjs";
import { Safety } from "../api/damebooru/models";

export interface CategorizedTag {
  name: string;
  category?: string;
}

export interface AutoTaggingResult {
  categorizedTags: CategorizedTag[];
  sources?: string[];
  safety?: Safety;
  confidence?: number; // 0 to 1
  provider: string;
  providerId: string;
}

export type SettingType = "text" | "number" | "boolean" | "password";
export type SettingValue = string | number | boolean;

export interface ProviderSetting {
  key: string;
  label: string;
  type: SettingType;
  description?: string;
  defaultValue: SettingValue;
  validators?: ValidatorFn[];
}

export interface TaggingProvider<
  T extends Record<string, SettingValue> = Record<string, SettingValue>,
> {
  readonly id: string;
  readonly name: string;
  readonly priority: number;
  /** Default enabled state for new installations */
  readonly defaultEnabled: boolean;

  // Define what settings this provider supports
  getSettingsSchema(): ProviderSetting[];

  // Handle current values
  getSettings(): T;
  updateSettings(settings: T): void;

  // Enabled state
  isEnabled(): boolean;
  setEnabled(enabled: boolean): void;

  canHandle(file: File): boolean;
  tag(file: File): Observable<AutoTaggingResult>;
}

export type TaggingStatus = "idle" | "tagging" | "completed" | "failed";
