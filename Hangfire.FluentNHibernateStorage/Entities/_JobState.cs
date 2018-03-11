﻿using System;

namespace Hangfire.FluentNHibernateStorage.Entities
{
    public class _JobState : Int32IdBase
    {
        public virtual _Job Job { get; set; }
        public virtual string Name { get; set; }
        public virtual string Reason { get; set; }
        public virtual DateTime CreatedAt { get; set; }
        public virtual string Data { get; set; }
    }
}