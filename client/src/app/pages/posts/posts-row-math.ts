import { VirtualRowPosition } from './posts.types';

function clamp(value: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, value));
}

function normalizeColumns(columns: number): number {
    return Math.max(1, columns);
}

function normalizeHeight(heightPx: number): number {
    return Math.max(1, heightPx);
}

function getPostRowsForItemCount(itemCount: number, columns: number): number {
    const normalizedItems = Math.max(0, itemCount);
    const normalizedColumns = normalizeColumns(columns);
    return Math.ceil(normalizedItems / normalizedColumns);
}

function getPageLocalRowTopPx(rowOffsetInPage: number, postRowHeightPx: number, separatorRowHeightPx: number): number {
    if (rowOffsetInPage <= 0) {
        return 0;
    }

    const normalizedPostRowHeightPx = normalizeHeight(postRowHeightPx);
    const normalizedSeparatorRowHeightPx = normalizeHeight(separatorRowHeightPx);
    return normalizedSeparatorRowHeightPx + (rowOffsetInPage - 1) * normalizedPostRowHeightPx;
}

function getRowOffsetInPageForLocalTopPx(
    localTopPx: number,
    rowsInPage: number,
    postRowHeightPx: number,
    separatorRowHeightPx: number
): number {
    if (rowsInPage <= 1) {
        return 0;
    }

    const normalizedPostRowHeightPx = normalizeHeight(postRowHeightPx);
    const normalizedSeparatorRowHeightPx = normalizeHeight(separatorRowHeightPx);
    const normalizedLocalTop = Math.max(0, localTopPx);

    if (normalizedLocalTop < normalizedSeparatorRowHeightPx) {
        return 0;
    }

    const postRowOffsetInPage = Math.floor((normalizedLocalTop - normalizedSeparatorRowHeightPx) / normalizedPostRowHeightPx) + 1;
    return clamp(postRowOffsetInPage, 1, rowsInPage - 1);
}

export function offsetToPage(offset: number, pageSize: number): number {
    return Math.floor(Math.max(0, offset) / pageSize) + 1;
}

export function getRowsForItemCount(itemCount: number, columns: number): number {
    return 1 + getPostRowsForItemCount(itemCount, columns);
}

export function getFullPageRowCount(pageSize: number, columns: number): number {
    return getRowsForItemCount(pageSize, columns);
}

export function getVirtualRowCount(totalCount: number | null, anchorHint: number, pageSize: number, columns: number): number {
    const normalizedColumns = normalizeColumns(columns);

    if (totalCount === null) {
        return getRowsForItemCount(pageSize, normalizedColumns);
    }

    if (totalCount <= 0) {
        return 0;
    }

    const totalPages = Math.ceil(totalCount / pageSize);
    const fullPageRows = getFullPageRowCount(pageSize, normalizedColumns);
    const fullPages = Math.max(0, totalPages - 1);
    const lastPageItems = totalCount - fullPages * pageSize;
    const lastPageRows = getRowsForItemCount(lastPageItems, normalizedColumns);

    return fullPages * fullPageRows + lastPageRows;
}

export function getVirtualRowPosition(
    rowIndex: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number
): VirtualRowPosition | null {
    if (rowCount <= 0) {
        return null;
    }

    const clampedIndex = clamp(Math.floor(rowIndex), 0, rowCount - 1);
    const normalizedColumns = normalizeColumns(columns);

    if (totalCount === null) {
        return {
            page: Math.max(1, anchorHint),
            rowOffsetInPage: clampedIndex,
            pageItemCount: pageSize,
        };
    }

    const normalizedTotalCount = Math.max(0, totalCount);
    const totalPages = Math.ceil(normalizedTotalCount / pageSize);
    if (totalPages <= 0) {
        return null;
    }

    const fullPageRows = getFullPageRowCount(pageSize, normalizedColumns);
    if (totalPages === 1) {
        return {
            page: 1,
            rowOffsetInPage: clampedIndex,
            pageItemCount: normalizedTotalCount,
        };
    }

    const fullPages = totalPages - 1;
    const rowsBeforeLast = fullPages * fullPageRows;
    if (clampedIndex < rowsBeforeLast) {
        const pageOffset = Math.floor(clampedIndex / fullPageRows);
        const rowOffsetInPage = clampedIndex - pageOffset * fullPageRows;
        return {
            page: pageOffset + 1,
            rowOffsetInPage,
            pageItemCount: pageSize,
        };
    }

    const lastPageItems = normalizedTotalCount - fullPages * pageSize;
    const lastPageRows = getRowsForItemCount(lastPageItems, normalizedColumns);
    return {
        page: totalPages,
        rowOffsetInPage: clamp(clampedIndex - rowsBeforeLast, 0, lastPageRows - 1),
        pageItemCount: lastPageItems,
    };
}

export function getPageForRowIndex(
    rowIndex: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number
): number {
    return getVirtualRowPosition(rowIndex, rowCount, totalCount, pageSize, columns, anchorHint)?.page ?? 1;
}

export function getFirstVisibleOffsetForRowIndex(
    rowIndex: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number
): number {
    const position = getVirtualRowPosition(rowIndex, rowCount, totalCount, pageSize, columns, anchorHint);
    if (!position) {
        return 0;
    }

    const pageBaseOffset = (position.page - 1) * pageSize;
    if (position.rowOffsetInPage === 0) {
        return pageBaseOffset;
    }

    const normalizedColumns = normalizeColumns(columns);
    return pageBaseOffset + (position.rowOffsetInPage - 1) * normalizedColumns;
}

export function getRowIndexForOffset(
    offset: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number
): number {
    if (rowCount <= 0) {
        return 0;
    }

    const normalizedColumns = normalizeColumns(columns);
    const normalizedOffset = Math.max(0, offset);
    const normalizedTotalCount = Math.max(0, totalCount ?? 0);
    const totalPages = totalCount === null ? 0 : Math.ceil(normalizedTotalCount / pageSize);

    const page = totalPages > 0
        ? clamp(offsetToPage(normalizedOffset, pageSize), 1, totalPages)
        : offsetToPage(normalizedOffset, pageSize);

    const pageBaseOffset = (page - 1) * pageSize;
    const inPageOffset = Math.max(0, normalizedOffset - pageBaseOffset);
    const rowOffsetInPage = Math.floor(inPageOffset / normalizedColumns) + 1;

    if (totalCount === null || totalPages <= 1) {
        return clamp(rowOffsetInPage, 0, rowCount - 1);
    }

    const fullPageRows = getFullPageRowCount(pageSize, normalizedColumns);
    if (page < totalPages) {
        const index = (page - 1) * fullPageRows + rowOffsetInPage;
        return clamp(index, 0, rowCount - 1);
    }

    const rowsBeforeLast = Math.max(0, (totalPages - 1) * fullPageRows);
    const lastPageItems = Math.max(1, normalizedTotalCount - (totalPages - 1) * pageSize);
    const lastPageRows = getRowsForItemCount(lastPageItems, normalizedColumns);
    const index = rowsBeforeLast + rowOffsetInPage;

    return clamp(index, rowsBeforeLast, rowsBeforeLast + lastPageRows - 1);
}

export function getVirtualContentHeightPx(
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number,
    postRowHeightPx: number,
    separatorRowHeightPx: number
): number {
    const normalizedRowCount = Math.max(0, Math.floor(rowCount));
    if (normalizedRowCount <= 0) {
        return 0;
    }

    const normalizedColumns = normalizeColumns(columns);
    const normalizedPostRowHeightPx = normalizeHeight(postRowHeightPx);
    const normalizedSeparatorRowHeightPx = normalizeHeight(separatorRowHeightPx);

    if (totalCount === null) {
        return normalizedSeparatorRowHeightPx + Math.max(0, normalizedRowCount - 1) * normalizedPostRowHeightPx;
    }

    const normalizedTotalCount = Math.max(0, totalCount);
    const totalPages = Math.ceil(normalizedTotalCount / pageSize);
    if (totalPages <= 0) {
        return 0;
    }

    const fullPagePostRows = getPostRowsForItemCount(pageSize, normalizedColumns);
    const fullPageHeightPx = normalizedSeparatorRowHeightPx + fullPagePostRows * normalizedPostRowHeightPx;
    const fullPages = Math.max(0, totalPages - 1);
    const lastPageItems = normalizedTotalCount - fullPages * pageSize;
    const lastPagePostRows = getPostRowsForItemCount(lastPageItems, normalizedColumns);
    const lastPageHeightPx = normalizedSeparatorRowHeightPx + lastPagePostRows * normalizedPostRowHeightPx;

    return fullPages * fullPageHeightPx + lastPageHeightPx;
}

export function getRowTopPxForIndex(
    rowIndex: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number,
    postRowHeightPx: number,
    separatorRowHeightPx: number
): number {
    const normalizedRowCount = Math.max(0, Math.floor(rowCount));
    if (normalizedRowCount <= 0) {
        return 0;
    }

    const clampedIndex = clamp(Math.floor(rowIndex), 0, normalizedRowCount - 1);
    const normalizedColumns = normalizeColumns(columns);
    const normalizedPostRowHeightPx = normalizeHeight(postRowHeightPx);
    const normalizedSeparatorRowHeightPx = normalizeHeight(separatorRowHeightPx);

    if (totalCount === null) {
        return getPageLocalRowTopPx(clampedIndex, normalizedPostRowHeightPx, normalizedSeparatorRowHeightPx);
    }

    const normalizedTotalCount = Math.max(0, totalCount);
    const totalPages = Math.ceil(normalizedTotalCount / pageSize);
    if (totalPages <= 0) {
        return 0;
    }

    const fullPageRows = getFullPageRowCount(pageSize, normalizedColumns);
    const fullPagePostRows = Math.max(0, fullPageRows - 1);
    const fullPageHeightPx = normalizedSeparatorRowHeightPx + fullPagePostRows * normalizedPostRowHeightPx;

    if (totalPages === 1) {
        return getPageLocalRowTopPx(clampedIndex, normalizedPostRowHeightPx, normalizedSeparatorRowHeightPx);
    }

    const fullPages = totalPages - 1;
    const rowsBeforeLast = fullPages * fullPageRows;
    if (clampedIndex < rowsBeforeLast) {
        const pageOffset = Math.floor(clampedIndex / fullPageRows);
        const rowOffsetInPage = clampedIndex - pageOffset * fullPageRows;
        return pageOffset * fullPageHeightPx
            + getPageLocalRowTopPx(rowOffsetInPage, normalizedPostRowHeightPx, normalizedSeparatorRowHeightPx);
    }

    const rowOffsetInLastPage = clampedIndex - rowsBeforeLast;
    return fullPages * fullPageHeightPx
        + getPageLocalRowTopPx(rowOffsetInLastPage, normalizedPostRowHeightPx, normalizedSeparatorRowHeightPx);
}

export function getRowIndexForScrollTop(
    scrollTopPx: number,
    rowCount: number,
    totalCount: number | null,
    pageSize: number,
    columns: number,
    anchorHint: number,
    postRowHeightPx: number,
    separatorRowHeightPx: number
): number {
    const normalizedRowCount = Math.max(0, Math.floor(rowCount));
    if (normalizedRowCount <= 0) {
        return 0;
    }

    const normalizedTopPx = Math.max(0, scrollTopPx);
    const normalizedColumns = normalizeColumns(columns);
    const normalizedPostRowHeightPx = normalizeHeight(postRowHeightPx);
    const normalizedSeparatorRowHeightPx = normalizeHeight(separatorRowHeightPx);

    if (totalCount === null) {
        return clamp(
            getRowOffsetInPageForLocalTopPx(
                normalizedTopPx,
                normalizedRowCount,
                normalizedPostRowHeightPx,
                normalizedSeparatorRowHeightPx
            ),
            0,
            normalizedRowCount - 1
        );
    }

    const normalizedTotalCount = Math.max(0, totalCount);
    const totalPages = Math.ceil(normalizedTotalCount / pageSize);
    if (totalPages <= 0) {
        return 0;
    }

    const fullPageRows = getFullPageRowCount(pageSize, normalizedColumns);
    const fullPagePostRows = Math.max(0, fullPageRows - 1);
    const fullPageHeightPx = normalizedSeparatorRowHeightPx + fullPagePostRows * normalizedPostRowHeightPx;

    if (totalPages === 1) {
        return clamp(
            getRowOffsetInPageForLocalTopPx(
                normalizedTopPx,
                fullPageRows,
                normalizedPostRowHeightPx,
                normalizedSeparatorRowHeightPx
            ),
            0,
            normalizedRowCount - 1
        );
    }

    const fullPages = totalPages - 1;
    const rowsBeforeLast = fullPages * fullPageRows;
    const fullPagesHeightPx = fullPages * fullPageHeightPx;
    if (normalizedTopPx < fullPagesHeightPx) {
        const pageOffset = Math.floor(normalizedTopPx / fullPageHeightPx);
        const localTopPx = normalizedTopPx - pageOffset * fullPageHeightPx;
        const rowOffsetInPage = getRowOffsetInPageForLocalTopPx(
            localTopPx,
            fullPageRows,
            normalizedPostRowHeightPx,
            normalizedSeparatorRowHeightPx
        );

        return clamp(pageOffset * fullPageRows + rowOffsetInPage, 0, normalizedRowCount - 1);
    }

    const lastPageItems = normalizedTotalCount - fullPages * pageSize;
    const lastPageRows = getRowsForItemCount(lastPageItems, normalizedColumns);
    const localTopPx = normalizedTopPx - fullPagesHeightPx;
    const lastPageRowOffsetInPage = getRowOffsetInPageForLocalTopPx(
        localTopPx,
        lastPageRows,
        normalizedPostRowHeightPx,
        normalizedSeparatorRowHeightPx
    );
    const rowIndex = rowsBeforeLast + lastPageRowOffsetInPage;

    return clamp(rowIndex, 0, normalizedRowCount - 1);
}
