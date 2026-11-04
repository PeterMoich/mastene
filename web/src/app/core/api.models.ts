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

export interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
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
}

export interface Settings {
  expiryNoticeDays: number[];
  defaultEscalationPct: number;
  maxEscalationPct: number;
  monthToMonthOnLapse: boolean;
  terminationNoticeDays: number;
  leaseTemplateVersion: string;
  /** Field names whose values are placeholders needing legal review. */
  legalReviewNeeded: string[];
}

export type SettingsRequest = Omit<Settings, 'legalReviewNeeded'>;
