﻿using System;

namespace Hangfire.FluentNHibernateStorage.Entities
{
    public abstract class KeyValueTypeBase<T> : Int32IdBase, IExpirableWithKey, IExpirableWithId
    {
        public virtual T Value { get; set; }
        public virtual string Key { get; set; }
        public virtual DateTime? ExpireAt { get; set; }
    }
}