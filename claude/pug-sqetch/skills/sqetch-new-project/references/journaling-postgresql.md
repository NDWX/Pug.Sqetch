# Journaling template: PostgreSQL

A complete set of the fifteen journaling statements for a PostgreSQL database. They keep one table,
`sqetch_journal`, with one row per event. Rename the table or add columns if the project needs to,
but keep the two queries' logic: they are what tell `sqetch-deploy` where a database stands,
including after a rollback.

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

Runs at the start of every deployment, so both statements are idempotent. `;;` alone on a line
separates them. `at_utc` is `timestamptz`: `sqetch-deploy` binds the instant as UTC, and a
`timestamp` column would store whatever the session's time zone made of it. A maintainer who
prefers the server's clock can write `now()` in place of `@utcTimestamp` in the inserts.

```sql
create table if not exists sqetch_journal (
	id bigserial primary key,
	project text not null,
	slot text not null,
	release text not null,
	plan text,
	step text,
	description text,
	at_utc timestamptz not null
)
;;
create index if not exists sqetch_journal_release on sqetch_journal ( project, release )
```

## Queries

### GetLatestRelease

Returns the release the database last reached and whether it was completed. It skips a release
that a rollback undid entirely. It keeps a release that a rollback undid only in part, which
happens when an earlier run had already committed some of its plans, since those plans are
still in place. A release counts as completed only when its `ReleaseDeployed` came after its
latest rollback.

```sql
select d.release,
	exists ( select 1 from sqetch_journal c
		where c.project = @project and c.release = d.release and c.slot = 'ReleaseDeployed'
			and c.id > coalesce( ( select max( u.id ) from sqetch_journal u
				where u.project = @project and u.release = d.release and u.slot = 'RolledBackRelease' ), 0 ) )
from sqetch_journal d
where d.project = @project and d.slot = 'DeployingRelease'
	and (
		not exists ( select 1 from sqetch_journal u
			where u.project = @project and u.release = d.release and u.slot = 'RolledBackRelease'
				and u.id > d.id )
		or exists ( select 1 from sqetch_journal p
			where p.project = @project and p.release = d.release and p.slot = 'PlanDeployed'
				and not exists ( select 1 from sqetch_journal r
					where r.project = @project and r.release = p.release and r.plan = p.plan
						and r.slot = 'RolledBackPlan' and r.id > p.id ) ) )
order by d.id desc
limit 1
```

### GetDeployedPlans

The plans of `@release` that are in place: deployed, and not rolled back since.

```sql
select distinct p.plan from sqetch_journal p
where p.project = @project and p.release = @release and p.slot = 'PlanDeployed'
	and not exists ( select 1 from sqetch_journal r
		where r.project = @project and r.release = p.release and r.plan = p.plan
			and r.slot = 'RolledBackPlan' and r.id > p.id )
```

## Deployment events

### DeployingRelease

```sql
insert into sqetch_journal ( project, slot, release, description, at_utc ) values ( @project, 'DeployingRelease', @release, @description, @utcTimestamp )
```

### DeployingPlan

```sql
insert into sqetch_journal ( project, slot, release, plan, description, at_utc ) values ( @project, 'DeployingPlan', @release, @plan, @description, @utcTimestamp )
```

### DeployingStep

```sql
insert into sqetch_journal ( project, slot, release, plan, step, description, at_utc ) values ( @project, 'DeployingStep', @release, @plan, @step, @description, @utcTimestamp )
```

### StepDeployed

```sql
insert into sqetch_journal ( project, slot, release, plan, step, description, at_utc ) values ( @project, 'StepDeployed', @release, @plan, @step, @description, @utcTimestamp )
```

### PlanDeployed

```sql
insert into sqetch_journal ( project, slot, release, plan, description, at_utc ) values ( @project, 'PlanDeployed', @release, @plan, @description, @utcTimestamp )
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
insert into sqetch_journal ( project, slot, release, plan, at_utc ) values ( @project, 'RollingBackPlan', @release, @plan, @utcTimestamp )
```

### RollingBackStep

```sql
insert into sqetch_journal ( project, slot, release, plan, step, at_utc ) values ( @project, 'RollingBackStep', @release, @plan, @step, @utcTimestamp )
```

### RolledBackStep

```sql
insert into sqetch_journal ( project, slot, release, plan, step, at_utc ) values ( @project, 'RolledBackStep', @release, @plan, @step, @utcTimestamp )
```

### RolledBackPlan

```sql
insert into sqetch_journal ( project, slot, release, plan, at_utc ) values ( @project, 'RolledBackPlan', @release, @plan, @utcTimestamp )
```

### RolledBackRelease

```sql
insert into sqetch_journal ( project, slot, release, at_utc ) values ( @project, 'RolledBackRelease', @release, @utcTimestamp )
```
