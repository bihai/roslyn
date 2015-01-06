﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Globalization;

namespace Microsoft.CodeAnalysis
{
    internal struct CodeAnalysisResourcesLocalizableErrorArgument : IFormattable, IMessageSerializable
    {
        private readonly string targetResourceId;

        internal CodeAnalysisResourcesLocalizableErrorArgument(string targetResourceId)
        {
            Debug.Assert(targetResourceId != null);
            this.targetResourceId = targetResourceId;
        }

        public override string ToString()
        {
            return ToString(null, null);
        }

        public string ToString(string format, IFormatProvider formatProvider)
        {
            if (targetResourceId != null)
            {
                return CodeAnalysisResources.ResourceManager.GetString(targetResourceId, formatProvider as System.Globalization.CultureInfo);
            }

            return string.Empty;
        }
    }
}