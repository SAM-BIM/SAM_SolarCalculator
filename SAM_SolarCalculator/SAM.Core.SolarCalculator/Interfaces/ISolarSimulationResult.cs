// SPDX-License-Identifier: LGPL-3.0-or-later
// Copyright (c) 2020–2026 Michal Dengusiak & Jakub Ziolkowski and contributors

using System;
using System.Collections.Generic;

namespace SAM.Core.SolarCalculator
{
    public interface ISolarSimulationResult : ISolarObject, IResult, IJSAMObject
    {
        List<DateTime> DateTimes { get; }
    }
}
