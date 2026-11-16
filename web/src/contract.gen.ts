// Generated from src/Latch.Core/Contract/Contract.cs by tools/Latch.ContractGen. Do not edit.

export type RuleKind = "exe" | "package";
export type AppStatus = "locked" | "unlocked" | "grace" | "hidden";
export type ThemeChoice = "system" | "light" | "dark";
export type UnlockMethod = "pin" | "hello" | "passkey";
export type HistoryKind = "unlocked" | "failed" | "lockoutStarted" | "promptCancelled" | "latchStarted" | "latchQuit" | "latchNotRunning";
export type AttentionKind = "notRunning" | "lockout" | "appMissing";
export type PromptPurpose = "app" | "openLatch" | "quit" | "changePin" | "turnOffHello" | "removePasskey" | "turnOffStartup";
export type UnlockOutcome = "ok" | "wrong" | "lockedOut" | "notVerified" | "gone";
export type ErrorCode = "unexpected" | "pinFormat" | "needsUnlock" | "passkeyFailed" | "passkeyUnavailable" | "helloUnavailable" | "helloFailed" | "notAProgram" | "notFound";
export type FixKind = "tryAgain" | "unlock" | "remove";

export interface Empty {
}

export interface FixAction {
  kind: FixKind;
  label: string;
}

export interface ApiError {
  code: ErrorCode;
  message: string;
  fix: FixAction | null;
}

export interface AppRow {
  id: string;
  name: string;
  iconUrl: string | null;
  status: AppStatus;
  relocksAt: string | null;
}

export interface AttentionItem {
  id: string;
  kind: AttentionKind;
  appId: string | null;
  appName: string | null;
  at: string;
  from: string | null;
}

export interface SettingsView {
  graceMinutes: number;
  relockOnClose: boolean;
  idleMinutes: number;
  startWithWindows: boolean;
  theme: ThemeChoice;
  lockAllHotkey: boolean;
  lockAllHotkeyTaken: boolean;
}

export interface MethodsView {
  pinSet: boolean;
  helloAvailable: boolean;
  helloOn: boolean;
  passkeyAvailable: boolean;
  passkeyOn: boolean;
}

export interface AppSnapshot {
  setupDone: boolean;
  apps: AppRow[];
  attention: AttentionItem[];
  settings: SettingsView;
  methods: MethodsView;
  historyRevision: number;
  version: string;
}

export interface PromptView {
  promptId: string;
  purpose: PromptPurpose;
  appName: string;
  icon: string | null;
  helloOn: boolean;
  passkeyOn: boolean;
  lockoutUntil: string | null;
  theme: ThemeChoice;
}

export interface InstalledApp {
  key: string;
  name: string;
  kind: RuleKind;
  path: string | null;
  packageFamilyName: string | null;
  aumid: string | null;
  iconUrl: string | null;
  locked: boolean;
}

export interface HistoryEntry {
  at: string;
  kind: HistoryKind;
  app: string | null;
  method: UnlockMethod | null;
  from: string | null;
}

export interface PinArgs {
  pin: string;
}

export interface IdArgs {
  id: string;
}

export interface PurposeArgs {
  purpose: PromptPurpose;
}

export interface SetLockedAppsArgs {
  apps: InstalledApp[];
}

export interface SettingsPatch {
  graceMinutes: number | null;
  relockOnClose: boolean | null;
  idleMinutes: number | null;
  startWithWindows: boolean | null;
  theme: ThemeChoice | null;
  lockAllHotkey: boolean | null;
}

export interface PromptPinArgs {
  promptId: string;
  pin: string;
}

export interface PromptIdArgs {
  promptId: string;
}

export interface PromptPaintedArgs {
  promptId: string;
  height: number;
}

export interface UnlockResult {
  outcome: UnlockOutcome;
  lockoutUntil: string | null;
}

export interface LatchApi {
  getState(): Promise<AppSnapshot>;
  windowReady(): Promise<Empty>;
  setPin(args: PinArgs): Promise<Empty>;
  turnOnHello(): Promise<boolean>;
  turnOffHello(): Promise<Empty>;
  addPasskey(): Promise<boolean>;
  removePasskey(): Promise<Empty>;
  finishSetup(): Promise<Empty>;
  listInstalledApps(): Promise<InstalledApp[]>;
  browseForProgram(): Promise<InstalledApp | null>;
  setLockedApps(args: SetLockedAppsArgs): Promise<Empty>;
  removeApp(args: IdArgs): Promise<Empty>;
  showApp(args: IdArgs): Promise<Empty>;
  getHistory(): Promise<HistoryEntry[]>;
  dismissAttention(args: IdArgs): Promise<Empty>;
  updateSettings(args: SettingsPatch): Promise<Empty>;
  requestUnlock(args: PurposeArgs): Promise<boolean>;
  submitPin(args: PromptPinArgs): Promise<UnlockResult>;
  usePasskey(args: PromptIdArgs): Promise<UnlockResult>;
  useHello(args: PromptIdArgs): Promise<UnlockResult>;
  cancelPrompt(args: PromptIdArgs): Promise<Empty>;
  promptPainted(args: PromptPaintedArgs): Promise<Empty>;
}

export type LatchMethod = keyof LatchApi;

export interface ServerEventMap {
  state: AppSnapshot;
  prompt: PromptView | null;
}
