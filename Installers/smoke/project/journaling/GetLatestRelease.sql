select j.release,
		( select count( * ) from sqetch_journal c
			where c.project = @project and c.release = j.release and c.slot = 'ReleaseDeployed' )
from sqetch_journal j
where j.project = @project and j.slot = 'DeployingRelease'
order by j.id desc
limit 1
