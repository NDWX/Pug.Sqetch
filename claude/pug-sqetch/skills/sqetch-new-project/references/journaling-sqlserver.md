# Journaling template: SQL Server

The fifteen required journaling statements, and the optional `JournalExists`, for a SQL Server or Azure SQL database. They
keep one table, `sqetch_journal`, with one row per event. Rename the table or add columns if the
project needs to, but keep the two queries' logic: they are what tell `sqetch-deploy` where a
database stands, including after a rollback.

This is the `sqlserver` driver's built-in template. The driver arrives in `sqetch-deploy` 0.2,
and `sqetch journaling init sqlserver` (`sqetch` 0.2) sets them all at once; use what follows
only with an older `sqetch`, or to read the SQL.

`plan` is a reserved word in T-SQL, so the template writes the column as `[plan]`; keep the brackets
in any statement that names it.

Write each block below to a file named after its slot, in a temporary folder outside the project
(here `/tmp/journaling-sql`), then set them all from inside the project folder:

```sh
for slot in PrepareJournal GetLatestRelease GetDeployedPlans \
            DeployingRelease DeployingPlan DeployingStep StepDeployed PlanDeployed ReleaseDeployed \
            RollingBackRelease RollingBackPlan RollingBackStep RolledBackStep RolledBackPlan RolledBackRelease
do
  sqetch journaling set "$slot" --file "/tmp/journaling-sql/$slot.sql"
done
sqetch journaling list        # every slot should now show as set
rm -r /tmp/journaling-sql     # sqetch keeps its own copy in the project's journaling/ folder
```

Each heading below is a slot name, and the block under it is that slot's SQL.

## Setup

### PrepareJournal

Runs at the start of every deployment, so both statements check before creating. `;;` alone on a
line separates them; journaling statements are never split on `GO`, which applies to step scripts
only. `at_utc` is `datetime2` holding UTC: `sqetch-deploy` binds the host's instant as `datetime2`,
to the tick. A maintainer who prefers the server's clock can write `sysutcdatetime()` in place of
`@utcTimestamp` in the inserts.

```sql
if object_id( N'sqetch_journal', N'U' ) is null
	create table sqetch_journal (
		id bigint identity primary key,
		project nvarchar(128) not null,
		slot nvarchar(32) not null,
		release nvarchar(128) not null,
		[plan] nvarchar(128),
		step nvarchar(128),
		description nvarchar(max),
		at_utc datetime2 not null
	)
;;
if not exists ( select 1 from sys.indexes
	where name = N'sqetch_journal_release' and object_id = object_id( N'sqetch_journal' ) )
	create index sqetch_journal_release on sqetch_journal ( project, release )
```

## Queries

### GetLatestRelease

Returns the release the database last reached and whether it was completed. It skips a release
that a rollback undid entirely. It keeps a release that a rollback undid only in part, which
happens when an earlier run had already committed some of its plans, since those plans are
still in place. A release counts as completed only when its `ReleaseDeployed` came after its
latest rollback; T-SQL has no boolean expression in a select list, so the second column is
`case when exists ( … ) then 1 else 0 end`.

```sql
select top (1) d.release,
	case when exists ( select 1 from sqetch_journal c
		where c.project = @project and c.release = d.release and c.slot = 'ReleaseDeployed'
			and c.id > coalesce( ( select max( u.id ) from sqetch_journal u
				where u.project = @project and u.release = d.release and u.slot = 'RolledBackRelease' ), 0 ) )
		then 1 else 0 end
from sqetch_journal d
where d.project = @project and d.slot = 'DeployingRelease'
	and (
		not exists ( select 1 from sqetch_journal u
			where u.project = @project and u.release = d.release and u.slot = 'RolledBackRelease'
				and u.id > d.id )
		or exists ( select 1 from sqetch_journal p
			where p.project = @project and p.release = d.release and p.slot = 'PlanDeployed'
				and not exists ( select 1 from sqetch_journal r
					where r.project = @project and r.release = p.release and r.[plan] = p.[plan]
						and r.slot = 'RolledBackPlan' and r.id > p.id ) ) )
order by d.id desc
```

### GetDeployedPlans

The plans of `@release` that are in place: deployed, and not rolled back since.

```sql
select distinct p.[plan] from sqetch_journal p
where p.project = @project and p.release = @release and p.slot = 'PlanDeployed'
	and not exists ( select 1 from sqetch_journal r
		where r.project = @project and r.release = p.release and r.[plan] = p.[plan]
			and r.slot = 'RolledBackPlan' and r.id > p.id )
```

### JournalExists

Optional, since 0.2: whether the journal table exists, so that `sqetch-deploy status` can tell a fresh database from one that has a journal.

```sql
select case when object_id( N'sqetch_journal', N'U' ) is null then 0 else 1 end
```

### AcquireLock

Optional, since 0.2: a query that takes the project's deployment lock, so a second concurrent deployment is refused (exit `34`). It returns one row whose first column is true when the lock was taken. The lock is released when the deployment ends, or at the latest when `sqetch-deploy` exits.

```sql
declare @resource nvarchar(255) = N'sqetch:' + @project;
declare @result int;
exec @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0;
select case when @result >= 0 then 1 when @result = -1 then 0 end;
```

### ReleaseLock

Optional, since 0.2: releases the deployment lock when the deployment ends; if it is unset or fails, the lock is released when `sqetch-deploy` exits and its database session ends.

```sql
declare @resource nvarchar(255) = N'sqetch:' + @project;
exec sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';
```

## Deployment events

### DeployingRelease

```sql
insert into sqetch_journal ( project, slot, release, description, at_utc ) values ( @project, 'DeployingRelease', @release, @description, @utcTimestamp )
```

### DeployingPlan

```sql
insert into sqetch_journal ( project, slot, release, [plan], description, at_utc ) values ( @project, 'DeployingPlan', @release, @plan, @description, @utcTimestamp )
```

### DeployingStep

```sql
insert into sqetch_journal ( project, slot, release, [plan], step, description, at_utc ) values ( @project, 'DeployingStep', @release, @plan, @step, @description, @utcTimestamp )
```

### StepDeployed

```sql
insert into sqetch_journal ( project, slot, release, [plan], step, description, at_utc ) values ( @project, 'StepDeployed', @release, @plan, @step, @description, @utcTimestamp )
```

### PlanDeployed

```sql
insert into sqetch_journal ( project, slot, release, [plan], description, at_utc ) values ( @project, 'PlanDeployed', @release, @plan, @description, @utcTimestamp )
```

### ReleaseDeployed

```sql
insert into sqetch_journal ( project, slot, release, description, at_utc ) values ( @project, 'ReleaseDeployed', @release, @description, @utcTimestamp )
```

## Rollback events

### RollingBackRelease

```sql
insert into sqetch_journal ( project, slot, release, at_utc ) values ( @project, 'RollingBackRelease', @release, @utcTimestamp )
```

### RollingBackPlan

```sql
insert into sqetch_journal ( project, slot, release, [plan], at_utc ) values ( @project, 'RollingBackPlan', @release, @plan, @utcTimestamp )
```

### RollingBackStep

```sql
insert into sqetch_journal ( project, slot, release, [plan], step, at_utc ) values ( @project, 'RollingBackStep', @release, @plan, @step, @utcTimestamp )
```

### RolledBackStep

```sql
insert into sqetch_journal ( project, slot, release, [plan], step, at_utc ) values ( @project, 'RolledBackStep', @release, @plan, @step, @utcTimestamp )
```

### RolledBackPlan

```sql
insert into sqetch_journal ( project, slot, release, [plan], at_utc ) values ( @project, 'RolledBackPlan', @release, @plan, @utcTimestamp )
```

### RolledBackRelease

```sql
insert into sqetch_journal ( project, slot, release, at_utc ) values ( @project, 'RolledBackRelease', @release, @utcTimestamp )
```
