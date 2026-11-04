// Typed mirrors of the API's DTOs (src/TenantManager.Application). Kept by hand in Phase 1;
// generating them from the OpenAPI document is planned once the API surface settles.

export type Role = 'Admin' | 'Landlord' | 'Tenant';
export type UnitStatus = 'Vacant' | 'Occupied';

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

export interface ListQuery {
  page?: number;
  pageSize?: number;
  /** Field name, prefixed with '-' for descending. */
  sort?: string;
  search?: string;
}

export interface Me {
  id: string;
  email: string;
  displayName: string;
  role: Role;
  tenantId: string | null;
}

/** Sign-in or refresh. The refresh token is an HttpOnly cookie the page can't read, so it isn't here. */
export interface SessionResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshTokenExpiresAt: string;
  user: Me;
}

export interface PropertyRequest {
  name: string;
  addressLine1: string;
  addressLine2: string | null;
  city: string;
  province: string;
  postalCode: string;
}

export interface PropertySummary {
  id: string;
  name: string;
  city: string;
  province: string;
  unitCount: number;
  occupiedCount: number;
  createdAt: string;
}

export interface Unit {
  id: string;
  propertyId: string;
  propertyName: string;
  label: string;
  rentAmount: number;
  depositAmount: number;
  status: UnitStatus;
}

export interface Property extends PropertyRequest {
  id: string;
  units: Unit[];
  createdAt: string;
  updatedAt: string | null;
}

export interface UnitRequest {
  label: string;
  rentAmount: number;
  depositAmount: number;
}

export interface TenantRequest {
  firstName: string;
  lastName: string;
  email: string;
  phone: string;
}

export interface Tenant extends TenantRequest {
  id: string;
  hasLogin: boolean;
  createdAt: string;
  updatedAt: string | null;
}

export interface TenantLoginCreated {
  userId: string;
  email: string;
  temporaryPassword: string;
}

export interface User {
  id: string;
  email: string;
  displayName: string;
  role: Role;
  tenantId: string | null;
  isLocked: boolean;
  createdAt: string;
}

export interface CreateUserRequest {
  email: string;
  displayName: string;
  role: 'Admin' | 'Landlord';
  password: string;
}

export const PROVINCES = [
  'Eastern Cape',
  'Free State',
  'Gauteng',
  'KwaZulu-Natal',
  'Limpopo',
  'Mpumalanga',
  'Northern Cape',
  'North West',
  'Western Cape',
] as const;

// ---- Leases (Phase 2) ----

export type LeaseStatus =
  | 'Draft'
  | 'PendingSignature'
  | 'Active'
  | 'Expiring'
  | 'Renewed'
  | 'Ended'
  | 'Terminated';

export const LEASE_STATUSES: LeaseStatus[] = [
  'Draft',
  'PendingSignature',
  'Active',
  'Expiring',
  'Renewed',
  'Ended',
  'Terminated',
];

/** What the current user may do next, worked out by the API. */
export type LeaseAction =
  | 'edit'
  | 'discard'
  | 'generate'
  | 'sign'
  | 'cancel'
  | 'renew'
  | 'terminate'
  | 'download';

export interface LeaseSummary {
  id: string;
  leaseNumber: string;
  version: number;
  status: LeaseStatus;
  isMonthToMonth: boolean;
  propertyId: string;
  propertyName: string;
  unitId: string;
  unitLabel: string;
  tenantId: string;
  tenantName: string;
  /** ISO date, e.g. 2026-03-01 */
  startDate: string;
  endDate: string;
  monthlyRent: number;
  terminatesOn: string | null;
}

export interface LeaseSignature {
  party: 'Landlord' | 'Tenant';
  signerName: string;
  signedAt: string;
}

export interface LeaseDocument {
  id: string;
  kind: 'Unsigned' | 'Signed';
  createdAt: string;
  sizeBytes: number;
}

export interface Lease extends LeaseSummary {
  propertyAddress: string;
  landlordName: string | null;
  depositAmount: number;
  escalationPct: number;
  previousLeaseId: string | null;
  renewalLeaseId: string | null;
  activatedOn: string | null;
  terminationReason: string | null;
  templateVersion: string | null;
  signatures: LeaseSignature[];
  documents: LeaseDocument[];
  expiryNoticesRecorded: number[];
  allowedActions: LeaseAction[];
  createdAt: string;
  updatedAt: string | null;
}

export interface LeaseRequest {
  unitId: string;
  tenantId: string;
  startDate: string;
  monthlyRent: number;
  depositAmount: number;
  escalationPct: number | null;
}

export interface RenewLeaseRequest {
  escalationPct: number | null;
  startDate: string | null;
}

export interface TerminateLeaseRequest {
  reason: string;
  lastDay: string;
}

export interface LeaseListQuery extends ListQuery {
  status?: LeaseStatus | '';
  propertyId?: string;
  tenantId?: string;
}

export interface LifecycleSummary {
  activated: number;
  renewed: number;
  markedExpiring: number;
  noticesRecorded: number;
  movedToMonthToMonth: number;
  ended: number;
  terminated: number;
  chargesRaised: number;
  overdueNoticesRecorded: number;
  issuesEscalated: number;
  issuesAutoClosed: number;
  remindersQueued: number;
  messagesSent: number;
}

export interface Settings {
  expiryNoticeDays: number[];
  defaultEscalationPct: number;
  maxEscalationPct: number;
  monthToMonthOnLapse: boolean;
  terminationNoticeDays: number;
  leaseTemplateVersion: string;
  rentDueDay: number;
  rentGraceDays: number;
  prorateRent: boolean;
  depositRefundDays: number;
  slaUrgentResponseHours: number;
  slaUrgentResolutionHours: number;
  slaHighResponseHours: number;
  slaHighResolutionHours: number;
  slaMediumResponseHours: number;
  slaMediumResolutionHours: number;
  slaLowResponseHours: number;
  slaLowResolutionHours: number;
  issueAutoCloseDays: number;
  rentReminderDaysBefore: number;
  /** Field names whose values are placeholders needing legal review. */
  legalReviewNeeded: string[];
}

export type SettingsRequest = Omit<Settings, 'legalReviewNeeded'>;

// ---- Rent, payments and deposits (Phase 3) ----

export type ChargeState = 'Paid' | 'Due' | 'Overdue';

export interface LedgerLine {
  /** yyyy-MM */
  period: string;
  amountDue: number;
  dueDate: string;
  daysCharged: number;
  prorated: boolean;
  paid: number;
  outstanding: number;
  state: ChargeState;
  overdueNoticeOn: string | null;
}

export interface Ledger {
  leaseId: string;
  leaseNumber: string;
  version: number;
  totalDue: number;
  totalPaid: number;
  /** Negative means the tenant is in credit. */
  balance: number;
  arrears: number;
  credit: number;
  graceDays: number;
  lines: LedgerLine[];
}

export type PaymentStatus = 'Submitted' | 'Verified' | 'Rejected';
export type PaymentAction = 'viewFile' | 'verify' | 'reject' | 'downloadReceipt';

export interface Payment {
  id: string;
  leaseId: string;
  leaseNumber: string;
  leaseVersion: number;
  tenantId: string;
  tenantName: string;
  propertyName: string;
  unitLabel: string;
  amount: number;
  paidOn: string;
  period: string | null;
  reference: string | null;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  status: PaymentStatus;
  rejectionReason: string | null;
  reviewedByName: string | null;
  reviewedAt: string | null;
  receiptNumber: string | null;
  createdAt: string;
  allowedActions: PaymentAction[];
}

export interface PaymentListQuery extends ListQuery {
  status?: PaymentStatus | '';
  leaseId?: string;
  propertyId?: string;
  period?: string;
}

export interface SubmitPayment {
  leaseId: string;
  amount: number;
  paidOn: string;
  period: string | null;
  reference: string | null;
  file: File;
}

export interface Arrears {
  leaseId: string;
  leaseNumber: string;
  version: number;
  tenantId: string;
  tenantName: string;
  propertyName: string;
  unitLabel: string;
  arrears: number;
  balance: number;
  oldestOverduePeriod: string;
  daysOverdue: number;
}

export type DepositStatus = 'AwaitingPayment' | 'Held' | 'PartiallyRefunded' | 'Refunded';

export interface Deposit {
  leaseNumber: string;
  amountAgreed: number;
  amountReceived: number;
  receivedOn: string | null;
  status: DepositStatus;
  deductions: { amount: number; reason: string; createdAt: string }[];
  totalDeducted: number;
  remaining: number;
  amountRefunded: number;
  refundedOn: string | null;
  leaseHasEnded: boolean;
  leaseEndedOn: string | null;
  refundDueBy: string | null;
  allowedActions: ('receive' | 'deduct' | 'refund')[];
}

/** Proof of payment rules, mirrored from the API (BR12) so users get instant feedback. */
export const PROOF_OF_PAYMENT_MAX_BYTES = 5 * 1024 * 1024;
export const PROOF_OF_PAYMENT_TYPES = ['application/pdf', 'image/jpeg', 'image/png'];

// ---- Issues, contractors and inspections (Phase 4) ----

export const ISSUE_CATEGORIES = ['Plumbing', 'Electrical', 'Appliance', 'Structural', 'Pest', 'Security', 'Garden', 'Other'] as const;
export type IssueCategory = (typeof ISSUE_CATEGORIES)[number];
export const ISSUE_PRIORITIES = ['Urgent', 'High', 'Medium', 'Low'] as const;
export type IssuePriority = (typeof ISSUE_PRIORITIES)[number];
export const ISSUE_STATUSES = ['Open', 'Acknowledged', 'InProgress', 'Resolved', 'Closed'] as const;
export type IssueStatus = (typeof ISSUE_STATUSES)[number];
export type IssueEventKind =
  | 'Opened' | 'Acknowledged' | 'Started' | 'ContractorAssigned' | 'ContractorRemoved' | 'PriorityChanged'
  | 'Resolved' | 'Reopened' | 'Closed' | 'AutoClosed' | 'ResponseTargetMissed' | 'ResolutionTargetMissed';
export type IssueAction =
  | 'acknowledge' | 'start' | 'resolve' | 'close' | 'reopen' | 'assignContractor' | 'changePriority' | 'comment'
  | 'addPhoto';

export interface IssueSummary {
  id: string;
  title: string;
  category: IssueCategory;
  priority: IssuePriority;
  status: IssueStatus;
  unitId: string;
  propertyName: string;
  unitLabel: string;
  tenantName: string | null;
  contractorName: string | null;
  openedAt: string;
  responseDueAt: string;
  resolutionDueAt: string;
  isEscalated: boolean;
  photoCount: number;
}

export interface IssuePhoto {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedByName: string;
  at: string;
}

export interface IssueComment {
  id: string;
  authorName: string;
  byTenant: boolean;
  body: string;
  at: string;
}

export interface IssueEvent {
  kind: IssueEventKind;
  byName: string;
  at: string;
  note: string | null;
}

export interface Issue {
  id: string;
  title: string;
  description: string;
  category: IssueCategory;
  priority: IssuePriority;
  status: IssueStatus;
  unitId: string;
  propertyName: string;
  unitLabel: string;
  leaseId: string | null;
  leaseNumber: string | null;
  tenantId: string | null;
  tenantName: string | null;
  reportedByName: string;
  contractor: { id: string; name: string; trade: string; phone: string } | null;
  openedAt: string;
  acknowledgedAt: string | null;
  startedAt: string | null;
  resolvedAt: string | null;
  closedAt: string | null;
  responseDueAt: string;
  resolutionDueAt: string;
  /** true on time, false late (or waiting past the target), null still waiting. */
  responseMet: boolean | null;
  resolutionMet: boolean | null;
  isEscalated: boolean;
  responseMissedAt: string | null;
  resolutionMissedAt: string | null;
  resolutionNote: string | null;
  reopenCount: number;
  autoCloseDays: number;
  photos: IssuePhoto[];
  comments: IssueComment[];
  events: IssueEvent[];
  allowedActions: IssueAction[];
}

export interface IssueListQuery extends ListQuery {
  status?: IssueStatus | '';
  priority?: IssuePriority | '';
  propertyId?: string;
  leaseId?: string;
  contractorId?: string;
  escalated?: boolean | '';
  openOnly?: boolean | '';
}

export interface CreateIssueRequest {
  leaseId: string | null;
  unitId: string | null;
  category: IssueCategory;
  priority: IssuePriority;
  title: string;
  description: string;
}

export interface SlaPriority {
  priority: IssuePriority;
  responseTargetHours: number;
  resolutionTargetHours: number;
  opened: number;
  acknowledged: number;
  acknowledgedOnTime: number;
  resolved: number;
  resolvedOnTime: number;
  averageResponseHours: number | null;
  averageResolutionHours: number | null;
  escalated: number;
  stillOpen: number;
}

export interface SlaReport {
  from: string;
  to: string;
  priorities: SlaPriority[];
}

export interface Contractor {
  id: string;
  name: string;
  trade: string;
  phone: string;
  email: string | null;
  notes: string | null;
  unfinishedIssues: number;
}

export interface ContractorRequest {
  name: string;
  trade: string;
  phone: string;
  email: string | null;
  notes: string | null;
}

export type InspectionKind = 'MoveIn' | 'MoveOut';
export type InspectionStatus = 'Draft' | 'Completed';
export const ITEM_CONDITIONS = ['Good', 'Fair', 'Poor', 'Damaged', 'Missing'] as const;
export type ItemCondition = (typeof ITEM_CONDITIONS)[number];

export interface InspectionSummary {
  id: string;
  kind: InspectionKind;
  status: InspectionStatus;
  inspectedOn: string;
  completedAt: string | null;
  tenantAcknowledgedAt: string | null;
  areas: number;
  areasChecked: number;
  photos: number;
}

export interface LeaseInspections {
  leaseNumber: string;
  items: InspectionSummary[];
  allowedActions: ('startMoveIn' | 'startMoveOut')[];
}

export interface InspectionPhoto {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  at: string;
}

export interface InspectionItem {
  id: string;
  area: string;
  order: number;
  condition: ItemCondition | null;
  notes: string | null;
  photos: InspectionPhoto[];
  moveInCondition: ItemCondition | null;
  moveInNotes: string | null;
  moveInPhotos: InspectionPhoto[];
}

export interface Inspection {
  id: string;
  leaseId: string;
  leaseNumber: string;
  kind: InspectionKind;
  status: InspectionStatus;
  inspectedOn: string;
  generalNotes: string | null;
  completedAt: string | null;
  completedByName: string | null;
  tenantAcknowledgedAt: string | null;
  tenantComment: string | null;
  propertyName: string;
  unitLabel: string;
  tenantName: string;
  moveInInspectionId: string | null;
  items: InspectionItem[];
  allowedActions: ('edit' | 'complete' | 'acknowledge')[];
}

/** Photo rules, mirrored from the API (BR12): JPG or PNG, up to 5 MB, five per issue or per inspection area. */
export const PHOTO_MAX_BYTES = 5 * 1024 * 1024;
export const PHOTO_TYPES = ['image/jpeg', 'image/png'];
export const MAX_PHOTOS = 5;

// ---- Messaging (Phase 5) ----

export type MessageDirection = 'Inbound' | 'Outbound';
export type MessageChannel = 'WhatsApp' | 'App';
export type MessageStatus = 'Queued' | 'Sent' | 'Delivered' | 'Read' | 'Failed' | 'Received';
export type ConsentSource = 'Tenant' | 'Landlord' | 'WhatsAppStop';

export interface ChatMessage {
  id: string;
  direction: MessageDirection;
  channel: MessageChannel;
  kind: 'Text' | 'Template';
  templateName: string | null;
  body: string;
  status: MessageStatus;
  error: string | null;
  at: string;
  sentAt: string | null;
  byName: string | null;
}

export interface ConsentEntry {
  granted: boolean;
  at: string;
  source: ConsentSource;
  recordedByName: string;
  textVersion: string;
  phone: string;
  note: string | null;
}

export interface ConsentStatus {
  granted: boolean;
  phone: string;
  textVersion: string;
  text: string;
  latest: ConsentEntry | null;
  history: ConsentEntry[];
}

export interface ConversationSummary {
  tenantId: string;
  tenantName: string;
  phone: string;
  lastMessageAt: string | null;
  lastMessagePreview: string | null;
  unread: number;
  inServiceWindow: boolean;
  consentGranted: boolean;
}

export interface Thread {
  tenantId: string;
  tenantName: string;
  phone: string;
  consent: ConsentStatus;
  inServiceWindow: boolean;
  serviceWindowEndsAt: string | null;
  /** A free-form reply goes to WhatsApp now (consent and inside the 24-hour window). */
  whatsAppTextAllowed: boolean;
  /** The fake WhatsApp provider is in use, so the landlord can simulate the tenant replying. */
  simulationAvailable: boolean;
  messages: ChatMessage[];
}

export interface MessageTemplate {
  name: string;
  description: string;
  body: string;
  parameterNames: string[];
  manual: boolean;
}
