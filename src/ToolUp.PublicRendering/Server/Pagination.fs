// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 98 — deterministic listing pagination.
///
/// A pure page-slicing helper for the listing surfaces (collection menus,
/// tag-index pages, projected data tables) that otherwise render every
/// item. `paginate` returns the requested page's slice plus a stable
/// `{ Page; PageCount; HasPrev; HasNext }` contract; `pageSize <= 0`
/// means "no pagination" (the whole list as a single page), so a caller
/// that doesn't opt in is unchanged (GP 11). Pure / deterministic — no
/// culture-sensitive ops, same input → same slice.
namespace ToolUp.PublicRendering

/// One page of a paginated list. `Page` and `PageCount` are 1-based;
/// `Items` is the slice for `Page`.
type PageSlice<'a> = {
    Items: 'a list
    Page: int
    PageCount: int
    HasPrev: bool
    HasNext: bool
}

module Pagination =

    /// Slice `items` into page `page` of size `pageSize`. `page` is
    /// clamped to `1 .. PageCount`; `pageSize <= 0` returns the whole
    /// list as a single page. An empty list is page 1 of 1 with no items.
    let paginate (pageSize: int) (page: int) (items: 'a list) : PageSlice<'a> =
        let total = List.length items

        if pageSize <= 0 then
            {
                Items = items
                Page = 1
                PageCount = 1
                HasPrev = false
                HasNext = false
            }
        else
            let pageCount = max 1 ((total + pageSize - 1) / pageSize)
            let clamped = max 1 (min page pageCount)

            let slice = items |> List.skip ((clamped - 1) * pageSize) |> List.truncate pageSize

            {
                Items = slice
                Page = clamped
                PageCount = pageCount
                HasPrev = clamped > 1
                HasNext = clamped < pageCount
            }