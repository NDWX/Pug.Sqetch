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
