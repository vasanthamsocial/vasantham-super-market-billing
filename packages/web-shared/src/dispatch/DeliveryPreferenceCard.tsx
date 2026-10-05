'use client';

import { useEffect, useState } from 'react';
import { api } from '../api';
import { useApiData } from '../admin/useApiData';
import { FulfilmentModeLabels, type DeliveryPreference, type Transporter } from '../types';
import { ActionForm, ErrorText } from '../ui';

/** How a debtor usually gets their goods; the counter fills it in on their bills. */
export function DeliveryPreferenceCard({ business, debtorId, canManage, canSeeTransporters }: { business: string | null; debtorId: string; canManage: boolean; canSeeTransporters: boolean }) {
  const preference = useApiData<DeliveryPreference>(business ? `${business}/debtors/${debtorId}/delivery` : null);
  const transporters = useApiData<Transporter[]>(business && canSeeTransporters ? `${business}/transporters` : null);
  const [mode, setMode] = useState('PICKUP');
  const [transporterId, setTransporterId] = useState('');
  const p = preference.data;
  // Start from what is saved; a reload of the same version keeps what is being chosen.
  const savedMode = p?.mode;
  const savedTransporter = p?.transporterId ?? '';
  const savedVersion = p?.rowVersion;
  useEffect(() => {
    if (savedMode) {
      setMode(savedMode);
      setTransporterId(savedTransporter);
    }
  }, [savedMode, savedTransporter, savedVersion]);
  const lorry = (transporters.data ?? []).find((t) => t.id === transporterId);

  return (
    <section className="sb-card" aria-labelledby={`delivery-heading-${debtorId}`} data-testid="delivery-preference">
      <h3 id={`delivery-heading-${debtorId}`}>Usual delivery</h3>
      <ErrorText error={preference.error} />
      {p ? (
        <p>
          {FulfilmentModeLabels[p.mode] ?? p.mode}
          {p.transporterName ? `: ${p.transporterName}${p.destinationBranch ? ` to ${p.destinationBranch}` : ''}` : ''}
          {p.deliveryAddress ? `, deliver to ${p.deliveryAddress}` : ''}
        </p>
      ) : null}
      {canManage && p ? (
        <ActionForm
          key={p.rowVersion}
          submitLabel="Save usual delivery"
          onSubmit={async (data) => {
            await api.put(`${business}/debtors/${debtorId}/delivery`, {
              mode,
              transporterId: mode === 'LORRY' ? transporterId || null : null,
              destinationBranchId: mode === 'LORRY' ? String(data.get('destinationBranchId') ?? '') || null : null,
              deliveryAddress: String(data.get('deliveryAddress') ?? '').trim() || null,
              rowVersion: p.rowVersion,
            });
            await preference.reload();
          }}
        >
          <div className="sb-form-row">
            <label className="sb-field">
              <span className="sb-field__label">Delivery</span>
              <select className="sb-input" value={mode} onChange={(e) => setMode(e.target.value)}>
                {Object.entries(FulfilmentModeLabels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
              </select>
            </label>
            {mode === 'LORRY' ? (
              <>
                <label className="sb-field">
                  <span className="sb-field__label">Lorry service</span>
                  <select className="sb-input" value={transporterId} onChange={(e) => setTransporterId(e.target.value)}>
                    <option value="">Choose...</option>
                    {(transporters.data ?? []).filter((t) => t.isActive).map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
                  </select>
                </label>
                <label className="sb-field">
                  <span className="sb-field__label">Destination branch</span>
                  <select className="sb-input" name="destinationBranchId" defaultValue={p.destinationBranchId ?? ''} key={transporterId}>
                    <option value="">Decide at booking</option>
                    {(lorry?.branches ?? []).filter((b) => b.isDestination && b.isActive).map((b) => <option key={b.id} value={b.id}>{b.name}, {b.city}</option>)}
                  </select>
                </label>
              </>
            ) : null}
          </div>
          <label className="sb-field">
            <span className="sb-field__label">Delivery address (if not their address)</span>
            <input className="sb-input" name="deliveryAddress" defaultValue={p.deliveryAddress ?? ''} />
          </label>
        </ActionForm>
      ) : null}
    </section>
  );
}
