﻿using Hangfire.FluentNHibernateStorage.Entities;

namespace Hangfire.FluentNHibernateStorage.Maps
{
    internal class SqlListMap : Gen1Map<_List, string>
    {
        public SqlListMap()
        {
            Table("`List`");
        }

        protected override int? ValueLength
        {
            get { return int.MaxValue; }
        }

        protected override bool UniqueKey
        {
            get { return false; }
        }

        protected override string KeyObjectName
        {
            get { return "IX_LIST_KEY"; }
        }

        protected override string TableName
        {
            get { return "`List`"; }
        }

        protected override bool ValueNullable
        {
            get { return true; }
        }
    }
}