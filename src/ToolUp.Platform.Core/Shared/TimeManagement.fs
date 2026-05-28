// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module Toolup.TimeManagement

open System

type TimeRange = {
    StartDate: DateTime
    EndDate: DateTime
}

type ComparisonPeriods = {
    CurrentPeriod: TimeRange
    PreviousPeriod: TimeRange
}

//TODO Add year to date option, with comparable last year to date

type TimePeriod =
    | Last1Week
    | Last2Weeks
    | Last4Weeks
    | Last8Weeks
    | Last13Weeks
    | Last26Weeks
    | Last52Weeks
    | All

    /// The length of time in weeks, represented as an integer.
    member this.Length =
        match this with
        | Last1Week -> 1
        | Last2Weeks -> 2
        | Last4Weeks -> 4
        | Last8Weeks -> 8
        | Last13Weeks -> 13
        | Last26Weeks -> 26
        | Last52Weeks -> 52
        | All -> 0 // Special case, represents all available data

    static member timePeriodToString =
        function
        | Last1Week -> "Last 1 week"
        | Last2Weeks -> "Last 2 weeks"
        | Last4Weeks -> "Last 4 weeks"
        | Last8Weeks -> "Last 8 weeks"
        | Last13Weeks -> "Last 13 weeks"
        | Last26Weeks -> "Last 26 weeks"
        | Last52Weeks -> "Last 52 weeks"
        | All -> "All"

    static member comparisonPeriodToString =
        function
        | Last1Week -> "Previous 1 week"
        | Last2Weeks -> "Previous 2 weeks"
        | Last4Weeks -> "Previous 4 weeks"
        | Last8Weeks -> "Previous 8 weeks"
        | Last13Weeks -> "Previous 13 weeks"
        | Last26Weeks -> "Previous 26 weeks"
        | Last52Weeks -> "Previous 52 weeks"
        | All -> "Not applicable"

    static member Parse(s: string) =
        match s with
        | "Last 1 week"
        | "Previous 1 week" -> Last1Week
        | "Last 2 weeks"
        | "Previous 2 weeks" -> Last2Weeks
        | "Last 4 weeks"
        | "Previous 4 weeks" -> Last4Weeks
        | "Last 8 weeks"
        | "Previous 8 weeks" -> Last8Weeks
        | "Last 13 weeks"
        | "Previous 13 weeks" -> Last13Weeks
        | "Last 26 weeks"
        | "Previous 26 weeks" -> Last26Weeks
        | "Last 52 weeks"
        | "Previous 52 weeks" -> Last52Weeks
        | "All" -> All
        | _ -> Last4Weeks

    static member GetDateRange (dates: DateTime array) (period: TimePeriod) : TimeRange =
        if isNull dates || dates.Length = 0 then
            {
                StartDate = DateTime.MinValue
                EndDate = DateTime.MinValue
            }
        else
            match period with
            | All ->
                let startDate = dates[0]
                let endDate = dates[dates.Length - 1]

                {
                    StartDate = startDate
                    EndDate = endDate
                }
            | _ ->
                let revDates = Array.rev dates
                let skipPeriod = period.Length
                let endDate = revDates[0]

                let startDate =
                    if revDates.Length > skipPeriod - 1 then
                        revDates[skipPeriod - 1]
                    else
                        revDates[revDates.Length - 1]

                {
                    StartDate = startDate
                    EndDate = endDate
                }

    static member GetComparisonPeriods (dates: DateTime array) (period: TimePeriod) : ComparisonPeriods =
        if isNull dates || dates.Length = 0 then
            {
                CurrentPeriod = {
                    StartDate = DateTime.MinValue
                    EndDate = DateTime.MinValue
                }
                PreviousPeriod = {
                    StartDate = DateTime.MinValue
                    EndDate = DateTime.MinValue
                }
            }
        else
            match period with
            | All ->
                let startDate = dates[0]
                let endDate = dates[dates.Length - 1]

                {
                    CurrentPeriod = {
                        StartDate = startDate
                        EndDate = endDate
                    }
                    PreviousPeriod = {
                        StartDate = DateTime.MinValue
                        EndDate = DateTime.MinValue
                    }
                }
            | _ ->
                let revDates = Array.rev dates
                let skipPeriod = period.Length
                let primaryEndDate = revDates[0]

                let primaryStartDate =
                    if revDates.Length > skipPeriod - 1 then
                        revDates[skipPeriod - 1]
                    else
                        revDates[revDates.Length - 1]

                let comparisonEndDate =
                    if revDates.Length > skipPeriod then
                        revDates[skipPeriod]
                    else
                        revDates[revDates.Length - 1]

                let comparisonStartDate =
                    if revDates.Length > (2 * skipPeriod) - 1 then
                        revDates[(2 * skipPeriod) - 1]
                    else
                        revDates[revDates.Length - 1]

                {
                    CurrentPeriod = {
                        StartDate = primaryStartDate
                        EndDate = primaryEndDate
                    }
                    PreviousPeriod = {
                        StartDate = comparisonStartDate
                        EndDate = comparisonEndDate
                    }
                }