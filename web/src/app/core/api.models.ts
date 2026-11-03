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
