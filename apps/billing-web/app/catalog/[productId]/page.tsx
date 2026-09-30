import { ProductDetailPanel, RequirePermission } from '@sb/web-shared';

export default async function Page({ params }: { params: Promise<{ productId: string }> }) {
  const { productId } = await params;
  return (
    <RequirePermission permission="catalog.view">
      <ProductDetailPanel productId={productId} />
    </RequirePermission>
  );
}