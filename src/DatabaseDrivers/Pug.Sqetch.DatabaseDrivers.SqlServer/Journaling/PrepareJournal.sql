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
