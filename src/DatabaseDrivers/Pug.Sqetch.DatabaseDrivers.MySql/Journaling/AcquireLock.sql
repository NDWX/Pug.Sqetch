select get_lock( concat( 'sqetch:', sha1( concat( database(), ':', @project ) ) ), 0 )
