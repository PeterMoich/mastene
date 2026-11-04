import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  CreateUserRequest,
  Lease,
  LeaseListQuery,
  LeaseRequest,
  LeaseSummary,
  LifecycleSummary,
  RenewLeaseRequest,
  Settings,
  SettingsRequest,
  TerminateLeaseRequest,
  ListQuery,
  PagedResult,
  Property,
  PropertyRequest,
  PropertySummary,
  Tenant,
  TenantLoginCreated,
  TenantRequest,
  Unit,
  UnitRequest,
  User,
} from './api.models';

const BASE = '/api/v1';

function toParams(query: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') params = params.set(key, String(value));
  }
  return params;
}

/** Typed client for the Tenant Manager API. One method per endpoint. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Properties and units
  listProperties(query: ListQuery = {}): Observable<PagedResult<PropertySummary>> {
    return this.http.get<PagedResult<PropertySummary>>(`${BASE}/properties`, { params: toParams(query) });
  }
  getProperty(id: string): Observable<Property> {
    return this.http.get<Property>(`${BASE}/properties/${id}`);
  }
  createProperty(body: PropertyRequest): Observable<Property> {
    return this.http.post<Property>(`${BASE}/properties`, body);
  }
  updateProperty(id: string, body: PropertyRequest): Observable<Property> {
    return this.http.put<Property>(`${BASE}/properties/${id}`, body);
  }
  deleteProperty(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/properties/${id}`);
  }
  addUnit(propertyId: string, body: UnitRequest): Observable<Unit> {
    return this.http.post<Unit>(`${BASE}/properties/${propertyId}/units`, body);
  }
  updateUnit(id: string, body: UnitRequest): Observable<Unit> {
    return this.http.put<Unit>(`${BASE}/units/${id}`, body);
  }
  deleteUnit(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/units/${id}`);
  }

  // Tenants
  listTenants(query: ListQuery = {}): Observable<PagedResult<Tenant>> {
    return this.http.get<PagedResult<Tenant>>(`${BASE}/tenants`, { params: toParams(query) });
  }
  getTenant(id: string): Observable<Tenant> {
    return this.http.get<Tenant>(`${BASE}/tenants/${id}`);
  }
  createTenant(body: TenantRequest): Observable<Tenant> {
    return this.http.post<Tenant>(`${BASE}/tenants`, body);
  }
  updateTenant(id: string, body: TenantRequest): Observable<Tenant> {
    return this.http.put<Tenant>(`${BASE}/tenants/${id}`, body);
  }
  deleteTenant(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/tenants/${id}`);
  }
  createTenantLogin(id: string): Observable<TenantLoginCreated> {
    return this.http.post<TenantLoginCreated>(`${BASE}/tenants/${id}/login`, {});
  }

  // Users (Admin)
  listUsers(query: ListQuery & { role?: string } = {}): Observable<PagedResult<User>> {
    return this.http.get<PagedResult<User>>(`${BASE}/users`, { params: toParams(query) });
  }
  createUser(body: CreateUserRequest): Observable<User> {
    return this.http.post<User>(`${BASE}/users`, body);
  }
  setUserLocked(id: string, locked: boolean): Observable<User> {
    return this.http.put<User>(`${BASE}/users/${id}/lock`, { locked });
  }
  setUserRole(id: string, role: 'Admin' | 'Landlord'): Observable<User> {
    return this.http.put<User>(`${BASE}/users/${id}/role`, { role });
  }

  // Leases
  listLeases(query: LeaseListQuery = {}): Observable<PagedResult<LeaseSummary>> {
    return this.http.get<PagedResult<LeaseSummary>>(`${BASE}/leases`, { params: toParams(query) });
  }
  getLease(id: string): Observable<Lease> {
    return this.http.get<Lease>(`${BASE}/leases/${id}`);
  }
  leaseVersions(id: string): Observable<LeaseSummary[]> {
    return this.http.get<LeaseSummary[]>(`${BASE}/leases/${id}/versions`);
  }
  createLease(body: LeaseRequest): Observable<Lease> {
    return this.http.post<Lease>(`${BASE}/leases`, body);
  }
  updateLease(id: string, body: LeaseRequest): Observable<Lease> {
    return this.http.put<Lease>(`${BASE}/leases/${id}`, body);
  }
  discardLease(id: string): Observable<void> {
    return this.http.delete<void>(`${BASE}/leases/${id}`);
  }
  generateLease(id: string): Observable<Lease> {
    return this.http.post<Lease>(`${BASE}/leases/${id}/generate`, {});
  }
  signLease(id: string): Observable<Lease> {
    return this.http.post<Lease>(`${BASE}/leases/${id}/sign`, {});
  }
  renewLease(id: string, body: RenewLeaseRequest): Observable<Lease> {
    return this.http.post<Lease>(`${BASE}/leases/${id}/renewals`, body);
  }
  terminateLease(id: string, body: TerminateLeaseRequest): Observable<Lease> {
    return this.http.post<Lease>(`${BASE}/leases/${id}/terminate`, body);
  }
  leaseDocument(leaseId: string, documentId: string): Observable<Blob> {
    return this.http.get(`${BASE}/leases/${leaseId}/documents/${documentId}`, { responseType: 'blob' });
  }
  runLeaseChecks(): Observable<LifecycleSummary> {
    return this.http.post<LifecycleSummary>(`${BASE}/lease-checks/run`, {});
  }

  // Settings
  getSettings(): Observable<Settings> {
    return this.http.get<Settings>(`${BASE}/settings`);
  }
  updateSettings(body: SettingsRequest): Observable<Settings> {
    return this.http.put<Settings>(`${BASE}/settings`, body);
  }
}
